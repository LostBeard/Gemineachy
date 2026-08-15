using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gemineachy.Games
{
    public enum ChessColor { White, Black }

    public enum PieceKind { None, Pawn, Knight, Bishop, Rook, Queen, King }

    /// <summary>A single board square's occupant. <see cref="Kind"/> == None means empty (Color is then
    /// meaningless). A value type so the 8x8 board copies cheaply for legality testing.</summary>
    public readonly struct Piece
    {
        public readonly ChessColor Color;
        public readonly PieceKind Kind;
        public Piece(ChessColor color, PieceKind kind) { Color = color; Kind = kind; }
        public bool IsNone => Kind == PieceKind.None;
        public static readonly Piece None = new(ChessColor.White, PieceKind.None);
    }

    /// <summary>One fully-specified move. Board coordinates are row 0..7 (row 0 = rank 8, top) and
    /// col 0..7 (col 0 = file a). <see cref="Uci"/> is the canonical wire form ("e2e4", "e1g1",
    /// "e7e8q").</summary>
    public class ChessMove
    {
        public int FromR, FromC, ToR, ToC;
        public PieceKind Promotion = PieceKind.None; // Q/R/B/N when a pawn promotes
        public bool IsEnPassant;
        public bool IsCastle;
        public bool IsDoublePush;
        public bool IsCapture;

        public static string Sq(int r, int c) => $"{(char)('a' + c)}{8 - r}";
        private char PromoChar => Promotion switch
        {
            PieceKind.Queen => 'q',
            PieceKind.Rook => 'r',
            PieceKind.Bishop => 'b',
            PieceKind.Knight => 'n',
            _ => '\0'
        };
        public string Uci => Sq(FromR, FromC) + Sq(ToR, ToC) + (Promotion == PieceKind.None ? "" : PromoChar.ToString());
    }

    /// <summary>
    /// A complete, rules-correct chess engine: full legal-move generation (including castling, en passant,
    /// and promotion), check / checkmate / stalemate detection, and the draw rules (fifty-move, threefold
    /// repetition, insufficient material). White moves up the board (toward row 0); Black moves down.
    /// In Gemineachy the human plays White and Gemini plays Black.
    /// </summary>
    public class ChessGame
    {
        private Piece[,] _board = new Piece[8, 8];
        public Piece[,] Board => _board;
        public ChessColor CurrentTurn { get; private set; } = ChessColor.White;
        public (int Row, int Col)? SelectedSquare { get; private set; }
        public string StatusMessage { get; private set; } = "White's turn to move.";
        public bool IsGameOver { get; private set; }
        /// <summary>Winner, or null for a draw (check <see cref="IsGameOver"/> to disambiguate "no winner yet").</summary>
        public ChessColor? Winner { get; private set; }
        /// <summary>Human-readable outcome once the game is over (e.g. "Checkmate - White wins", "Stalemate - draw").</summary>
        public string? Outcome { get; private set; }
        /// <summary>Half-moves applied. A stable identity for "the position changed".</summary>
        public int Ply { get; private set; }

        // Castling rights: [WK, WQ, BK, BQ].
        private readonly bool[] _castle = new bool[4];
        private (int r, int c)? _ep;        // en-passant target square (the square a pawn skipped over)
        private int _halfmoveClock;         // for the fifty-move rule (plies since last pawn move/capture)
        private int _fullmove = 1;
        private readonly Dictionary<string, int> _repetition = new(); // placement|turn|castle|ep -> count
        public List<string> MoveHistory { get; } = new();

        public event Action? OnStateChanged;

        public ChessGame() => InitializeBoard();

        #region Setup
        public void InitializeBoard()
        {
            _board = new Piece[8, 8];
            PieceKind[] back = { PieceKind.Rook, PieceKind.Knight, PieceKind.Bishop, PieceKind.Queen,
                                 PieceKind.King, PieceKind.Bishop, PieceKind.Knight, PieceKind.Rook };
            for (int c = 0; c < 8; c++)
            {
                _board[0, c] = new Piece(ChessColor.Black, back[c]); // rank 8
                _board[1, c] = new Piece(ChessColor.Black, PieceKind.Pawn);
                _board[6, c] = new Piece(ChessColor.White, PieceKind.Pawn);
                _board[7, c] = new Piece(ChessColor.White, back[c]); // rank 1
            }
            CurrentTurn = ChessColor.White;
            for (int i = 0; i < 4; i++) _castle[i] = true;
            _ep = null;
            _halfmoveClock = 0;
            _fullmove = 1;
            _repetition.Clear();
            MoveHistory.Clear();
            SelectedSquare = null;
            IsGameOver = false;
            Winner = null;
            Outcome = null;
            Ply = 0;
            StatusMessage = "White's turn to move.";
            RecordRepetition();
            OnStateChanged?.Invoke();
        }
        #endregion

        /// <summary>Replace the position from a FEN string (placement, turn, castling, ep, clocks). Used by
        /// tests (perft) and any "set up this position" need.</summary>
        public void LoadFen(string fen)
        {
            var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _board = new Piece[8, 8];
            int r = 0, c = 0;
            foreach (var ch in parts[0])
            {
                if (ch == '/') { r++; c = 0; }
                else if (char.IsDigit(ch)) c += ch - '0';
                else
                {
                    var color = char.IsUpper(ch) ? ChessColor.White : ChessColor.Black;
                    var kind = char.ToLowerInvariant(ch) switch
                    {
                        'p' => PieceKind.Pawn, 'n' => PieceKind.Knight, 'b' => PieceKind.Bishop,
                        'r' => PieceKind.Rook, 'q' => PieceKind.Queen, 'k' => PieceKind.King, _ => PieceKind.None
                    };
                    _board[r, c++] = new Piece(color, kind);
                }
            }
            CurrentTurn = (parts.Length > 1 && parts[1] == "b") ? ChessColor.Black : ChessColor.White;
            var rights = parts.Length > 2 ? parts[2] : "-";
            _castle[0] = rights.Contains('K'); _castle[1] = rights.Contains('Q');
            _castle[2] = rights.Contains('k'); _castle[3] = rights.Contains('q');
            _ep = (parts.Length > 3 && parts[3] != "-") ? FromName(parts[3]) : null;
            _halfmoveClock = parts.Length > 4 && int.TryParse(parts[4], out var hm) ? hm : 0;
            _fullmove = parts.Length > 5 && int.TryParse(parts[5], out var fm) ? fm : 1;
            _repetition.Clear();
            MoveHistory.Clear();
            SelectedSquare = null;
            IsGameOver = false; Winner = null; Outcome = null; Ply = 0;
            RecordRepetition();
            EvaluateEnd();
            OnStateChanged?.Invoke();
        }

        /// <summary>Deep copy of the full game state (used by perft/search).</summary>
        public ChessGame Clone()
        {
            var g = new ChessGame();
            g._board = (Piece[,])_board.Clone();
            g.CurrentTurn = CurrentTurn;
            Array.Copy(_castle, g._castle, 4);
            g._ep = _ep;
            g._halfmoveClock = _halfmoveClock;
            g._fullmove = _fullmove;
            g.IsGameOver = IsGameOver; g.Winner = Winner; g.Outcome = Outcome; g.Ply = Ply;
            g.StatusMessage = StatusMessage;
            g._repetition.Clear();
            foreach (var kv in _repetition) g._repetition[kv.Key] = kv.Value;
            return g;
        }

        /// <summary>Count leaf nodes at the given depth (perft) - a move-generation correctness check.</summary>
        public long Perft(int depth)
        {
            if (depth == 0) return 1;
            var moves = GetLegalMoves();
            if (depth == 1) return moves.Count;
            long nodes = 0;
            foreach (var m in moves)
            {
                var g = Clone();
                g.ApplyMove(m);
                nodes += g.Perft(depth - 1);
            }
            return nodes;
        }

        #region Helpers
        public static bool InBounds(int r, int c) => r >= 0 && r < 8 && c >= 0 && c < 8;
        public static bool IsLight(int r, int c) => (r + c) % 2 == 0; // a1 (row7,col0) is dark; a8 light
        private static ChessColor Opp(ChessColor c) => c == ChessColor.White ? ChessColor.Black : ChessColor.White;
        private static int Forward(ChessColor c) => c == ChessColor.White ? -1 : 1; // row delta moving forward
        private static int PromoRow(ChessColor c) => c == ChessColor.White ? 0 : 7;
        private static int StartPawnRow(ChessColor c) => c == ChessColor.White ? 6 : 1;

        private static (int r, int c) FromName(string sq) => (8 - (sq[1] - '0'), sq[0] - 'a');
        #endregion

        #region Attack / check detection (operate on any board array so temp boards can be tested)
        /// <summary>Is square (r,c) attacked by any piece of <paramref name="by"/> on <paramref name="b"/>?</summary>
        private static bool IsAttacked(Piece[,] b, int r, int c, ChessColor by)
        {
            // Pawns: a pawn of `by` attacks diagonally in its forward direction. Invert to find attackers.
            int pr = r - Forward(by); // the row a `by` pawn would sit on to attack (r,c)
            foreach (int dc in new[] { -1, 1 })
            {
                int pc = c + dc;
                if (InBounds(pr, pc))
                {
                    var p = b[pr, pc];
                    if (!p.IsNone && p.Color == by && p.Kind == PieceKind.Pawn) return true;
                }
            }
            // Knights
            foreach (var (dr, dc) in KnightDeltas)
            {
                int nr = r + dr, nc = c + dc;
                if (InBounds(nr, nc)) { var p = b[nr, nc]; if (!p.IsNone && p.Color == by && p.Kind == PieceKind.Knight) return true; }
            }
            // King (adjacent)
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int nr = r + dr, nc = c + dc;
                    if (InBounds(nr, nc)) { var p = b[nr, nc]; if (!p.IsNone && p.Color == by && p.Kind == PieceKind.King) return true; }
                }
            // Sliding: bishop/queen on diagonals, rook/queen on orthogonals.
            foreach (var (dr, dc) in DiagDeltas)
                if (SlideHits(b, r, c, dr, dc, by, PieceKind.Bishop)) return true;
            foreach (var (dr, dc) in OrthoDeltas)
                if (SlideHits(b, r, c, dr, dc, by, PieceKind.Rook)) return true;
            return false;
        }

        private static bool SlideHits(Piece[,] b, int r, int c, int dr, int dc, ChessColor by, PieceKind straightOrDiag)
        {
            int nr = r + dr, nc = c + dc;
            while (InBounds(nr, nc))
            {
                var p = b[nr, nc];
                if (!p.IsNone)
                {
                    if (p.Color == by && (p.Kind == straightOrDiag || p.Kind == PieceKind.Queen)) return true;
                    return false; // blocked by any other piece
                }
                nr += dr; nc += dc;
            }
            return false;
        }

        private static (int r, int c) FindKing(Piece[,] b, ChessColor color)
        {
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (b[r, c].Kind == PieceKind.King && b[r, c].Color == color) return (r, c);
            return (-1, -1); // should never happen in a legal game
        }

        private static bool InCheck(Piece[,] b, ChessColor color)
        {
            var (kr, kc) = FindKing(b, color);
            return kr >= 0 && IsAttacked(b, kr, kc, Opp(color));
        }

        public bool IsInCheck(ChessColor color) => InCheck(_board, color);

        private static readonly (int, int)[] KnightDeltas =
            { (-2, -1), (-2, 1), (-1, -2), (-1, 2), (1, -2), (1, 2), (2, -1), (2, 1) };
        private static readonly (int, int)[] DiagDeltas = { (-1, -1), (-1, 1), (1, -1), (1, 1) };
        private static readonly (int, int)[] OrthoDeltas = { (-1, 0), (1, 0), (0, -1), (0, 1) };
        #endregion

        #region Move generation
        /// <summary>All fully-legal moves for the side to move (own king never left in check).</summary>
        public IReadOnlyList<ChessMove> GetLegalMoves()
        {
            var pseudo = GeneratePseudoLegal(CurrentTurn);
            var legal = new List<ChessMove>(pseudo.Count);
            foreach (var m in pseudo)
            {
                var nb = ApplyToBoard(_board, m, CurrentTurn);
                if (!InCheck(nb, CurrentTurn)) legal.Add(m);
            }
            return legal;
        }

        private List<ChessMove> GeneratePseudoLegal(ChessColor color)
        {
            var moves = new List<ChessMove>();
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    var p = _board[r, c];
                    if (p.IsNone || p.Color != color) continue;
                    switch (p.Kind)
                    {
                        case PieceKind.Pawn: GenPawn(r, c, color, moves); break;
                        case PieceKind.Knight: GenLeaper(r, c, color, KnightDeltas, moves); break;
                        case PieceKind.King: GenLeaper(r, c, color, AllKingDeltas, moves); GenCastling(r, c, color, moves); break;
                        case PieceKind.Bishop: GenSlider(r, c, color, DiagDeltas, moves); break;
                        case PieceKind.Rook: GenSlider(r, c, color, OrthoDeltas, moves); break;
                        case PieceKind.Queen: GenSlider(r, c, color, DiagDeltas, moves); GenSlider(r, c, color, OrthoDeltas, moves); break;
                    }
                }
            return moves;
        }

        private static readonly (int, int)[] AllKingDeltas =
            { (-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1) };

        private void GenLeaper(int r, int c, ChessColor color, (int, int)[] deltas, List<ChessMove> moves)
        {
            foreach (var (dr, dc) in deltas)
            {
                int nr = r + dr, nc = c + dc;
                if (!InBounds(nr, nc)) continue;
                var t = _board[nr, nc];
                if (t.IsNone || t.Color != color)
                    moves.Add(new ChessMove { FromR = r, FromC = c, ToR = nr, ToC = nc, IsCapture = !t.IsNone });
            }
        }

        private void GenSlider(int r, int c, ChessColor color, (int, int)[] deltas, List<ChessMove> moves)
        {
            foreach (var (dr, dc) in deltas)
            {
                int nr = r + dr, nc = c + dc;
                while (InBounds(nr, nc))
                {
                    var t = _board[nr, nc];
                    if (t.IsNone) moves.Add(new ChessMove { FromR = r, FromC = c, ToR = nr, ToC = nc });
                    else { if (t.Color != color) moves.Add(new ChessMove { FromR = r, FromC = c, ToR = nr, ToC = nc, IsCapture = true }); break; }
                    nr += dr; nc += dc;
                }
            }
        }

        private void GenPawn(int r, int c, ChessColor color, List<ChessMove> moves)
        {
            int fwd = Forward(color);
            int one = r + fwd;
            // Forward one (and two from start), only onto empty squares.
            if (InBounds(one, c) && _board[one, c].IsNone)
            {
                AddPawnAdvance(r, c, one, c, color, false, moves);
                int two = r + 2 * fwd;
                if (r == StartPawnRow(color) && _board[two, c].IsNone)
                    moves.Add(new ChessMove { FromR = r, FromC = c, ToR = two, ToC = c, IsDoublePush = true });
            }
            // Captures (incl. promotion), and en passant.
            foreach (int dc in new[] { -1, 1 })
            {
                int nc = c + dc;
                if (!InBounds(one, nc)) continue;
                var t = _board[one, nc];
                if (!t.IsNone && t.Color != color)
                    AddPawnAdvance(r, c, one, nc, color, true, moves);
                else if (_ep is { } ep && ep.r == one && ep.c == nc)
                    moves.Add(new ChessMove { FromR = r, FromC = c, ToR = one, ToC = nc, IsEnPassant = true, IsCapture = true });
            }
        }

        private void AddPawnAdvance(int fr, int fc, int tr, int tc, ChessColor color, bool capture, List<ChessMove> moves)
        {
            if (tr == PromoRow(color))
                foreach (var promo in new[] { PieceKind.Queen, PieceKind.Rook, PieceKind.Bishop, PieceKind.Knight })
                    moves.Add(new ChessMove { FromR = fr, FromC = fc, ToR = tr, ToC = tc, Promotion = promo, IsCapture = capture });
            else
                moves.Add(new ChessMove { FromR = fr, FromC = fc, ToR = tr, ToC = tc, IsCapture = capture });
        }

        private void GenCastling(int r, int c, ChessColor color, List<ChessMove> moves)
        {
            int home = color == ChessColor.White ? 7 : 0;
            if (r != home || c != 4) return;                 // king must be on its home square
            if (InCheck(_board, color)) return;              // can't castle out of check
            int kIdx = color == ChessColor.White ? 0 : 2;    // WK / BK
            int qIdx = color == ChessColor.White ? 1 : 3;    // WQ / BQ
            // King-side: squares f,g empty; king doesn't pass through/into attack; rook on h.
            if (_castle[kIdx] && _board[home, 5].IsNone && _board[home, 6].IsNone
                && _board[home, 7].Kind == PieceKind.Rook && _board[home, 7].Color == color
                && !IsAttacked(_board, home, 5, Opp(color)) && !IsAttacked(_board, home, 6, Opp(color)))
                moves.Add(new ChessMove { FromR = home, FromC = 4, ToR = home, ToC = 6, IsCastle = true });
            // Queen-side: squares b,c,d empty; king passes through d,c; rook on a.
            if (_castle[qIdx] && _board[home, 3].IsNone && _board[home, 2].IsNone && _board[home, 1].IsNone
                && _board[home, 0].Kind == PieceKind.Rook && _board[home, 0].Color == color
                && !IsAttacked(_board, home, 3, Opp(color)) && !IsAttacked(_board, home, 2, Opp(color)))
                moves.Add(new ChessMove { FromR = home, FromC = 4, ToR = home, ToC = 2, IsCastle = true });
        }

        /// <summary>Return a COPY of <paramref name="b"/> with <paramref name="m"/> applied (piece movement,
        /// en-passant removal, castling rook hop, promotion). Used both for legality testing and the real
        /// apply. Does not touch game state (rights/clocks) - that's done in <see cref="ApplyMove"/>.</summary>
        private static Piece[,] ApplyToBoard(Piece[,] b, ChessMove m, ChessColor color)
        {
            var nb = (Piece[,])b.Clone();
            var moving = nb[m.FromR, m.FromC];
            nb[m.FromR, m.FromC] = Piece.None;
            if (m.IsEnPassant) nb[m.FromR, m.ToC] = Piece.None; // captured pawn sits beside the mover
            if (m.Promotion != PieceKind.None) moving = new Piece(color, m.Promotion);
            nb[m.ToR, m.ToC] = moving;
            if (m.IsCastle)
            {
                int home = m.FromR;
                if (m.ToC == 6) { nb[home, 5] = nb[home, 7]; nb[home, 7] = Piece.None; }      // king-side rook h->f
                else { nb[home, 3] = nb[home, 0]; nb[home, 0] = Piece.None; }                  // queen-side rook a->d
            }
            return nb;
        }
        #endregion

        #region Applying moves
        /// <summary>Apply a move given in UCI ("e2e4", "e1g1", "e7e8q"); tolerant of separators ("e2-e4").
        /// Returns true if it matched a legal move.</summary>
        public bool TryMoveUci(string uci)
        {
            var m = MatchUci(uci);
            if (m == null) return false;
            ApplyMove(m);
            return true;
        }

        /// <summary>Find the legal move matching a UCI string, or null.</summary>
        public ChessMove? MatchUci(string uci)
        {
            if (string.IsNullOrWhiteSpace(uci)) return null;
            var s = new string(uci.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
            if (s.Length < 4) return null;
            return GetLegalMoves().FirstOrDefault(m => m.Uci == s);
        }

        /// <summary>True if a legal move from (fr,fc) to (tr,tc) is a pawn promotion (so the UI can ask the
        /// user which piece before committing, instead of auto-queening).</summary>
        public bool IsPromotionMove(int fr, int fc, int tr, int tc) =>
            GetLegalMoves().Any(m => m.FromR == fr && m.FromC == fc && m.ToR == tr && m.ToC == tc && m.Promotion != PieceKind.None);

        /// <summary>Programmatic move by board coordinates (used by the UI click path). Promotion defaults
        /// to Queen unless specified. Returns true if legal.</summary>
        public bool TryMove(int fr, int fc, int tr, int tc, PieceKind promotion = PieceKind.Queen)
        {
            var legal = GetLegalMoves();
            var m = legal.FirstOrDefault(x => x.FromR == fr && x.FromC == fc && x.ToR == tr && x.ToC == tc
                                              && (x.Promotion == PieceKind.None || x.Promotion == promotion));
            if (m == null) return false;
            ApplyMove(m);
            return true;
        }

        private void ApplyMove(ChessMove m)
        {
            if (IsGameOver) return;
            var mover = _board[m.FromR, m.FromC];
            bool isCapture = m.IsCapture || m.IsEnPassant;

            _board = ApplyToBoard(_board, m, CurrentTurn);

            // Castling rights: king move clears both; rook leaving a corner (or being captured there) clears that side.
            UpdateCastlingRights(m, mover.Color);

            // En passant target: only set on a double push.
            _ep = m.IsDoublePush ? (m.FromR + Forward(CurrentTurn), m.FromC) : null;

            // Fifty-move clock.
            _halfmoveClock = (mover.Kind == PieceKind.Pawn || isCapture) ? 0 : _halfmoveClock + 1;
            if (CurrentTurn == ChessColor.Black) _fullmove++;

            MoveHistory.Add(m.Uci);
            Ply++;
            SelectedSquare = null;
            CurrentTurn = Opp(CurrentTurn);
            RecordRepetition();
            EvaluateEnd();
            OnStateChanged?.Invoke();
        }

        private void UpdateCastlingRights(ChessMove m, ChessColor moverColor)
        {
            // If a king moved, clear both rights for that color.
            if (m.FromR == (moverColor == ChessColor.White ? 7 : 0) && m.FromC == 4 &&
                _board[m.ToR, m.ToC].Kind == PieceKind.King)
            {
                if (moverColor == ChessColor.White) { _castle[0] = _castle[1] = false; }
                else { _castle[2] = _castle[3] = false; }
            }
            // Any move FROM or TO a rook home square kills that castling right (rook moved or was captured).
            void Touch(int r, int c)
            {
                if (r == 7 && c == 7) _castle[0] = false;
                else if (r == 7 && c == 0) _castle[1] = false;
                else if (r == 0 && c == 7) _castle[2] = false;
                else if (r == 0 && c == 0) _castle[3] = false;
            }
            Touch(m.FromR, m.FromC);
            Touch(m.ToR, m.ToC);
        }

        private void EvaluateEnd()
        {
            var legal = GetLegalMoves();
            bool inCheck = InCheck(_board, CurrentTurn);
            if (legal.Count == 0)
            {
                IsGameOver = true;
                if (inCheck)
                {
                    Winner = Opp(CurrentTurn);
                    Outcome = $"Checkmate - {Winner} wins";
                }
                else { Winner = null; Outcome = "Stalemate - draw"; }
                StatusMessage = Outcome;
                return;
            }
            if (_halfmoveClock >= 100) { EndDraw("Draw - fifty-move rule"); return; }
            if (IsThreefold()) { EndDraw("Draw - threefold repetition"); return; }
            if (InsufficientMaterial()) { EndDraw("Draw - insufficient material"); return; }

            StatusMessage = $"{CurrentTurn}'s turn to move." + (inCheck ? " (check!)" : "");
        }

        private void EndDraw(string outcome)
        {
            IsGameOver = true; Winner = null; Outcome = outcome; StatusMessage = outcome;
        }
        #endregion

        #region Draw detection
        private string PositionKey()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    var p = _board[r, c];
                    sb.Append(p.IsNone ? '.' : PieceChar(p));
                }
            sb.Append('|').Append(CurrentTurn == ChessColor.White ? 'w' : 'b');
            sb.Append('|').Append(_castle[0] ? "K" : "").Append(_castle[1] ? "Q" : "")
              .Append(_castle[2] ? "k" : "").Append(_castle[3] ? "q" : "");
            sb.Append('|').Append(_ep is { } ep ? ChessMove.Sq(ep.r, ep.c) : "-");
            return sb.ToString();
        }

        private void RecordRepetition()
        {
            var key = PositionKey();
            _repetition[key] = _repetition.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private bool IsThreefold() => _repetition.TryGetValue(PositionKey(), out var n) && n >= 3;

        private bool InsufficientMaterial()
        {
            var minors = new List<(ChessColor color, PieceKind kind, bool lightSquare)>();
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    var p = _board[r, c];
                    if (p.IsNone || p.Kind == PieceKind.King) continue;
                    if (p.Kind is PieceKind.Pawn or PieceKind.Rook or PieceKind.Queen) return false; // enough material
                    minors.Add((p.Color, p.Kind, IsLight(r, c)));
                }
            if (minors.Count == 0) return true;                       // K vs K
            if (minors.Count == 1) return true;                       // K+minor vs K
            // K+B vs K+B with both bishops on the same color square is a draw; also any number of same-color bishops.
            if (minors.All(m => m.kind == PieceKind.Bishop) && minors.Select(m => m.lightSquare).Distinct().Count() == 1)
                return true;
            return false;
        }
        #endregion

        #region UI selection
        /// <summary>Squares to highlight: with nothing selected, the pieces that have a legal move; with a
        /// piece selected, that piece's legal destination squares. Encoded as r*8+c.</summary>
        public IEnumerable<int> CurrentTargets()
        {
            var legal = GetLegalMoves();
            if (SelectedSquare is not { } sel)
                return legal.Select(m => m.FromR * 8 + m.FromC).Distinct();
            return legal.Where(m => m.FromR == sel.Row && m.FromC == sel.Col)
                        .Select(m => m.ToR * 8 + m.ToC).Distinct();
        }

        /// <summary>Handle a board click at (row,col): select one of your pieces, or move the selected piece
        /// to a legal destination (auto-queen on promotion). Only acts for the side to move.</summary>
        public void SelectSquare(int row, int col)
        {
            if (IsGameOver || !InBounds(row, col)) return;
            var legal = GetLegalMoves();
            var here = _board[row, col];

            if (SelectedSquare is { } sel)
            {
                // Clicking a legal destination applies the move.
                if (legal.Any(m => m.FromR == sel.Row && m.FromC == sel.Col && m.ToR == row && m.ToC == col))
                {
                    TryMove(sel.Row, sel.Col, row, col); // auto-queen; ApplyMove fires OnStateChanged
                    return;
                }
                // Clicking another of your pieces reselects; clicking elsewhere clears.
                if (!here.IsNone && here.Color == CurrentTurn && legal.Any(m => m.FromR == row && m.FromC == col))
                {
                    SelectedSquare = (row, col);
                    StatusMessage = $"Selected {ChessMove.Sq(row, col)}. Choose a destination.";
                }
                else SelectedSquare = null;
                OnStateChanged?.Invoke();
                return;
            }

            if (!here.IsNone && here.Color == CurrentTurn && legal.Any(m => m.FromR == row && m.FromC == col))
            {
                SelectedSquare = (row, col);
                StatusMessage = $"Selected {ChessMove.Sq(row, col)}. Choose a destination.";
                OnStateChanged?.Invoke();
            }
        }
        #endregion

        #region Text rendering for the agent
        private static char PieceChar(Piece p)
        {
            char ch = p.Kind switch
            {
                PieceKind.Pawn => 'p', PieceKind.Knight => 'n', PieceKind.Bishop => 'b',
                PieceKind.Rook => 'r', PieceKind.Queen => 'q', PieceKind.King => 'k', _ => '.'
            };
            return p.Color == ChessColor.White ? char.ToUpperInvariant(ch) : ch;
        }

        /// <summary>Forsyth-Edwards Notation of the current position (placement, turn, castling, ep, clocks).</summary>
        public string ToFen()
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 8; r++)
            {
                int empty = 0;
                for (int c = 0; c < 8; c++)
                {
                    var p = _board[r, c];
                    if (p.IsNone) { empty++; continue; }
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(PieceChar(p));
                }
                if (empty > 0) sb.Append(empty);
                if (r < 7) sb.Append('/');
            }
            sb.Append(CurrentTurn == ChessColor.White ? " w " : " b ");
            var rights = $"{(_castle[0] ? "K" : "")}{(_castle[1] ? "Q" : "")}{(_castle[2] ? "k" : "")}{(_castle[3] ? "q" : "")}";
            sb.Append(rights.Length == 0 ? "-" : rights).Append(' ');
            sb.Append(_ep is { } ep ? ChessMove.Sq(ep.r, ep.c) : "-").Append(' ');
            sb.Append(_halfmoveClock).Append(' ').Append(_fullmove);
            return sb.ToString();
        }

        /// <summary>Text board + FEN + legal UCI move list for relaying state to Gemini.</summary>
        public string GetBoardStateText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Chess board. Uppercase = White, lowercase = Black, '.' = empty. Files a-h (left-right), ranks 8-1 (top-bottom).");
            sb.AppendLine();
            sb.AppendLine("    a b c d e f g h");
            for (int r = 0; r < 8; r++)
            {
                sb.Append($" {8 - r}  ");
                for (int c = 0; c < 8; c++)
                {
                    var p = _board[r, c];
                    sb.Append(p.IsNone ? '.' : PieceChar(p)).Append(' ');
                }
                sb.AppendLine($" {8 - r}");
            }
            sb.AppendLine("    a b c d e f g h");
            sb.AppendLine();
            sb.AppendLine($"FEN: {ToFen()}");
            sb.AppendLine($"Turn: {CurrentTurn}");
            if (IsGameOver)
            {
                sb.AppendLine($"GAME OVER - {Outcome}");
            }
            else
            {
                if (InCheck(_board, CurrentTurn)) sb.AppendLine($"{CurrentTurn} is in CHECK.");
                var legal = GetLegalMoves().Select(m => m.Uci).OrderBy(u => u).ToList();
                sb.AppendLine($"Legal moves ({legal.Count}), UCI notation: {string.Join(", ", legal)}");
            }
            sb.AppendLine($"Status: {StatusMessage}");
            return sb.ToString();
        }
        #endregion
    }
}
