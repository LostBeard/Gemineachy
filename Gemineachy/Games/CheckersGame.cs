using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gemineachy.Games
{
    public enum PieceType { None, Red, Black, RedKing, BlackKing }

    public enum Player { Red, Black }

    /// <summary>
    /// A single legal move as a path of PDN 1-32 square numbers. A simple move is two squares
    /// (from, to); a capture (jump) is two-or-more squares with the jumped squares in <see cref="Captured"/>.
    /// </summary>
    public class CheckersMove
    {
        public List<int> Path { get; } = new();
        public List<int> Captured { get; } = new();
        public bool IsCapture => Captured.Count > 0;
        public int From => Path[0];
        public int To => Path[^1];
        /// <summary>PDN movetext, e.g. "11-15" (simple) or "11x18x25" (multi-jump).</summary>
        public string Pdn => string.Join(IsCapture ? "x" : "-", Path);
        public CheckersMove Clone()
        {
            var m = new CheckersMove();
            m.Path.AddRange(Path);
            m.Captured.AddRange(Captured);
            return m;
        }
    }

    /// <summary>
    /// 8x8 English draughts (American checkers): men move/capture diagonally forward, kings one square
    /// in any diagonal direction, captures are mandatory, multi-jumps must be completed, reaching the
    /// far rank crowns a king (and ends the turn). Red moves up the board (toward row 0), Black down.
    /// </summary>
    public class CheckersGame
    {
        public PieceType[,] Board { get; private set; } = new PieceType[8, 8];
        public Player CurrentTurn { get; private set; } = Player.Red;
        public (int Row, int Col)? SelectedSquare { get; private set; }
        public string StatusMessage { get; private set; } = "Red's turn to move.";
        public bool IsGameOver { get; private set; }
        public Player? Winner { get; private set; }
        /// <summary>Number of half-moves (plies) applied this game. A stable identity for "the position changed".</summary>
        public int Ply { get; private set; }

        // PDN meta tags and movetext history
        public Dictionary<string, string> PdnTags { get; private set; } = new();
        public List<string> MoveHistory { get; private set; } = new();
        private int _movePairCounter = 1;

        // In-progress UI selection: the path of squares clicked so far (for click-through multi-jumps).
        private readonly List<int> _selectedPath = new();

        public event Action? OnStateChanged;

        public CheckersGame() => InitializeBoard();

        public void InitializeBoard(string eventName = "Casual Text Match", string redName = "Player Red", string blackName = "Gemini (Black)")
        {
            PdnTags.Clear();
            PdnTags["Event"] = eventName;
            PdnTags["Date"] = DateTime.Now.ToString("yyyy.MM.dd");
            PdnTags["Black"] = blackName;
            PdnTags["Red"] = redName;
            PdnTags["Result"] = "*";

            MoveHistory.Clear();
            _movePairCounter = 1;
            _selectedPath.Clear();
            SelectedSquare = null;
            IsGameOver = false;
            Winner = null;
            Ply = 0;

            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    Board[r, c] = PieceType.None;

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 8; c++)
                    if (IsDark(r, c)) Board[r, c] = PieceType.Black;

            for (int r = 5; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if (IsDark(r, c)) Board[r, c] = PieceType.Red;

            CurrentTurn = Player.Red;
            StatusMessage = "Red's turn to move.";
            OnStateChanged?.Invoke();
        }

        #region Coordinate helpers
        public static bool IsDark(int row, int col) => (row + col) % 2 == 1;
        public static bool InBounds(int row, int col) => row >= 0 && row < 8 && col >= 0 && col < 8;

        /// <summary>(row,col) -> PDN square number 1-32, or 0 for a light (unused) square.</summary>
        public static int ToSquare(int row, int col) => IsDark(row, col) ? (row * 4) + (col / 2) + 1 : 0;

        /// <summary>PDN square number 1-32 -> (row,col).</summary>
        public static (int Row, int Col) FromSquare(int square)
        {
            int idx = square - 1;
            int row = idx / 4;
            int half = idx % 4;
            int col = row % 2 == 0 ? half * 2 + 1 : half * 2; // dark square in that row
            return (row, col);
        }
        #endregion

        #region Piece helpers
        private static bool IsRed(PieceType p) => p == PieceType.Red || p == PieceType.RedKing;
        private static bool IsBlack(PieceType p) => p == PieceType.Black || p == PieceType.BlackKing;
        private static bool IsKing(PieceType p) => p == PieceType.RedKing || p == PieceType.BlackKing;
        private static Player OwnerOf(PieceType p) => IsRed(p) ? Player.Red : Player.Black;
        private bool BelongsToCurrent(PieceType p) => p != PieceType.None && OwnerOf(p) == CurrentTurn;

        /// <summary>Diagonal row directions a piece may move/capture in.</summary>
        private static int[] RowDirs(PieceType p) => p switch
        {
            PieceType.Red => new[] { -1 },              // Red men move up
            PieceType.Black => new[] { 1 },             // Black men move down
            PieceType.RedKing or PieceType.BlackKing => new[] { -1, 1 },
            _ => Array.Empty<int>(),
        };
        #endregion

        #region Legal move generation
        /// <summary>All legal moves for the side to move. Enforces mandatory capture: if any capture
        /// exists, only captures are returned.</summary>
        public IReadOnlyList<CheckersMove> GetLegalMoves()
        {
            var captures = new List<CheckersMove>();
            var simples = new List<CheckersMove>();
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    var p = Board[r, c];
                    if (!BelongsToCurrent(p)) continue;
                    var start = ToSquare(r, c);
                    var pieceCaptures = new List<CheckersMove>();
                    BuildCaptures(r, c, p, new CheckersMove { Path = { start } }, pieceCaptures);
                    if (pieceCaptures.Count > 0) { captures.AddRange(pieceCaptures); continue; }
                    // simple moves only matter if no captures exist anywhere (checked after the loop)
                    foreach (var dr in RowDirs(p))
                    {
                        foreach (var dc in new[] { -1, 1 })
                        {
                            int nr = r + dr, nc = c + dc;
                            if (InBounds(nr, nc) && Board[nr, nc] == PieceType.None)
                            {
                                var m = new CheckersMove();
                                m.Path.Add(start);
                                m.Path.Add(ToSquare(nr, nc));
                                simples.Add(m);
                            }
                        }
                    }
                }
            }
            return captures.Count > 0 ? captures : simples;
        }

        /// <summary>Recursively enumerate capture sequences from (r,c) for piece <paramref name="p"/>.
        /// Captured pieces are treated as removed for the remainder of the chain (standard rule).</summary>
        private void BuildCaptures(int r, int c, PieceType p, CheckersMove current, List<CheckersMove> results)
        {
            bool extended = false;
            foreach (var dr in RowDirs(p))
            {
                foreach (var dc in new[] { -1, 1 })
                {
                    int mr = r + dr, mc = c + dc;       // square being jumped
                    int lr = r + 2 * dr, lc = c + 2 * dc; // landing square
                    if (!InBounds(lr, lc)) continue;
                    if (Board[lr, lc] != PieceType.None) continue;
                    var midSq = ToSquare(mr, mc);
                    if (current.Captured.Contains(midSq)) continue; // already jumped in this chain
                    var mid = Board[mr, mc];
                    if (mid == PieceType.None) continue;
                    if (OwnerOf(mid) == CurrentTurn) continue; // can't jump own piece

                    // A man that reaches the crown row is promoted and the turn ends (no further jump).
                    bool crowns = !IsKing(p) && ((p == PieceType.Red && lr == 0) || (p == PieceType.Black && lr == 7));

                    var next = current.Clone();
                    next.Path.Add(ToSquare(lr, lc));
                    next.Captured.Add(midSq);
                    extended = true;

                    if (crowns)
                    {
                        results.Add(next);
                    }
                    else
                    {
                        int before = results.Count;
                        // temporarily reflect the capture on the board for deeper search
                        var savedMid = Board[mr, mc];
                        var savedFrom = Board[r, c];
                        Board[mr, mc] = PieceType.None;
                        Board[r, c] = PieceType.None;
                        Board[lr, lc] = p;
                        BuildCaptures(lr, lc, p, next, results);
                        Board[lr, lc] = PieceType.None;
                        Board[r, c] = savedFrom;
                        Board[mr, mc] = savedMid;
                        if (results.Count == before) results.Add(next); // no further jump -> this chain ends here
                    }
                }
            }
            _ = extended;
        }
        #endregion

        #region Applying moves
        /// <summary>Apply a move given its full path of PDN squares (validated against legal moves).
        /// Returns true if applied.</summary>
        public bool TryMovePath(IReadOnlyList<int> path)
        {
            if (IsGameOver || path == null || path.Count < 2) return false;
            var legal = GetLegalMoves();
            var match = legal.FirstOrDefault(m => m.Path.SequenceEqual(path));
            if (match == null) return false;
            ApplyMove(match);
            return true;
        }

        /// <summary>Apply a move identified by its start and end squares (picks a matching legal move;
        /// for multi-jumps the end square is the final landing square). Returns true if applied.</summary>
        public bool TryMove(int fromSquare, int toSquare)
        {
            if (IsGameOver) return false;
            var legal = GetLegalMoves();
            var match = legal.FirstOrDefault(m => m.From == fromSquare && m.To == toSquare);
            if (match == null) return false;
            ApplyMove(match);
            return true;
        }

        private void ApplyMove(CheckersMove move)
        {
            var (fr, fc) = FromSquare(move.From);
            var piece = Board[fr, fc];
            Board[fr, fc] = PieceType.None;
            foreach (var capSq in move.Captured)
            {
                var (cr, cc) = FromSquare(capSq);
                Board[cr, cc] = PieceType.None;
            }
            var (tr, tc) = FromSquare(move.To);
            // crown if a man reached the far rank
            if (piece == PieceType.Red && tr == 0) piece = PieceType.RedKing;
            else if (piece == PieceType.Black && tr == 7) piece = PieceType.BlackKing;
            Board[tr, tc] = piece;

            RecordMove(move.Pdn);
            Ply++;

            _selectedPath.Clear();
            SelectedSquare = null;
            CurrentTurn = CurrentTurn == Player.Red ? Player.Black : Player.Red;
            EvaluateGameEndAndStatus();
            OnStateChanged?.Invoke();
        }

        private void EvaluateGameEndAndStatus()
        {
            var moves = GetLegalMoves();
            if (moves.Count == 0)
            {
                IsGameOver = true;
                Winner = CurrentTurn == Player.Red ? Player.Black : Player.Red;
                PdnTags["Result"] = Winner == Player.Red ? "1-0" : "0-1";
                StatusMessage = $"Game over - {Winner} wins ({(Winner == Player.Red ? "Red" : "Black")} - opponent has no legal moves).";
            }
            else
            {
                bool mustCapture = moves[0].IsCapture;
                StatusMessage = $"{CurrentTurn}'s turn to move." + (mustCapture ? " (a capture is available and must be taken)" : "");
            }
        }
        #endregion

        #region UI selection (click-through, supports multi-jumps)
        /// <summary>The squares clicked so far in an in-progress selection (for highlighting multi-jumps).</summary>
        public IReadOnlyList<int> SelectedPath => _selectedPath;

        /// <summary>
        /// Squares to highlight right now: if nothing is selected, the pieces that can move; if a piece
        /// (or partial jump path) is selected, the legal next-step landing squares.
        /// </summary>
        public IEnumerable<int> CurrentTargets()
        {
            var legal = GetLegalMoves();
            if (_selectedPath.Count == 0)
                return legal.Select(m => m.From).Distinct();
            var prefix = _selectedPath;
            return legal
                .Where(m => m.Path.Count > prefix.Count && m.Path.Take(prefix.Count).SequenceEqual(prefix))
                .Select(m => m.Path[prefix.Count])
                .Distinct();
        }

        /// <summary>Convenience for tool/programmatic single moves via (row,col) coordinates.</summary>
        public bool SelectSquareAndMove(int row, int col, int rowDest, int colDest)
            => TryMove(ToSquare(row, col), ToSquare(rowDest, colDest));

        /// <summary>Handle a board click at (row,col): select a piece, or extend/complete the move path.</summary>
        public void SelectSquare(int row, int col)
        {
            if (IsGameOver || !InBounds(row, col) || !IsDark(row, col)) return;
            var sq = ToSquare(row, col);
            var legal = GetLegalMoves();

            if (_selectedPath.Count == 0)
            {
                if (legal.Any(m => m.From == sq))
                {
                    _selectedPath.Add(sq);
                    SelectedSquare = (row, col);
                    StatusMessage = $"Selected {sq}. Choose a destination.";
                    OnStateChanged?.Invoke();
                }
                return;
            }

            // clicking the selected piece again, or another of your movable pieces -> reselect
            if (sq == _selectedPath[0] || legal.Any(m => m.From == sq && sq != _selectedPath[^1]))
            {
                if (legal.Any(m => m.From == sq))
                {
                    _selectedPath.Clear();
                    _selectedPath.Add(sq);
                    SelectedSquare = FromSquare(sq) is var (rr, cc2) ? (rr, cc2) : null;
                    StatusMessage = $"Selected {sq}. Choose a destination.";
                    OnStateChanged?.Invoke();
                    return;
                }
            }

            var candidate = _selectedPath.Append(sq).ToList();
            var matchesPrefix = legal.Where(m => m.Path.Count >= candidate.Count
                                                 && m.Path.Take(candidate.Count).SequenceEqual(candidate)).ToList();
            if (matchesPrefix.Count == 0)
            {
                StatusMessage = "Invalid move. Try again.";
                OnStateChanged?.Invoke();
                return;
            }
            _selectedPath.Clear();
            _selectedPath.AddRange(candidate);
            SelectedSquare = (row, col);

            // if the path now exactly equals a complete legal move, apply it
            var complete = matchesPrefix.FirstOrDefault(m => m.Path.SequenceEqual(candidate));
            if (complete != null && !matchesPrefix.Any(m => m.Path.Count > candidate.Count))
            {
                ApplyMove(complete);
            }
            else
            {
                StatusMessage = $"Continue the jump from {sq}.";
                OnStateChanged?.Invoke();
            }
        }
        #endregion

        #region PDN + text rendering
        private void RecordMove(string pdnMove)
        {
            // Red moves first in this match, so a Red move starts a numbered pair.
            if (CurrentTurn == Player.Red)
            {
                MoveHistory.Add($"{_movePairCounter}. {pdnMove}");
            }
            else
            {
                if (MoveHistory.Count == 0) MoveHistory.Add($"1. ... {pdnMove}");
                else MoveHistory[^1] += $" {pdnMove}";
                _movePairCounter++;
            }
        }

        public string GetPdnFormat()
        {
            var sb = new StringBuilder();
            foreach (var tag in PdnTags) sb.AppendLine($"[{tag.Key} \"{tag.Value}\"]");
            sb.AppendLine();
            sb.AppendLine(string.Join(" ", MoveHistory));
            return sb.ToString();
        }

        /// <summary>
        /// Text board for relaying state to Gemini. Includes PDN square numbers, whose turn it is,
        /// capture counts, and the exact list of legal moves in PDN (so the agent moves legally).
        /// </summary>
        public string GetBoardStateText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Checkers board (English draughts). Uppercase = king. r/R = Red, b/B = Black. '.' = empty dark square, ' ' = light square.");
            sb.AppendLine("Squares use standard PDN 1-32 numbering (shown in the legal-move list).");
            sb.AppendLine();
            sb.AppendLine("   a b c d e f g h");
            for (int r = 0; r < 8; r++)
            {
                sb.Append($"{r + 1}  ");
                for (int c = 0; c < 8; c++)
                {
                    char symbol = !IsDark(r, c) ? ' ' : Board[r, c] switch
                    {
                        PieceType.Black => 'b',
                        PieceType.Red => 'r',
                        PieceType.BlackKing => 'B',
                        PieceType.RedKing => 'R',
                        _ => '.'
                    };
                    sb.Append($"{symbol} ");
                }
                sb.AppendLine();
            }

            int redCount = 0, blackCount = 0;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if (IsRed(Board[r, c])) redCount++;
                    if (IsBlack(Board[r, c])) blackCount++;
                }

            sb.AppendLine();
            sb.AppendLine($"Turn: {CurrentTurn}");
            sb.AppendLine($"Pieces remaining -> Red: {redCount}, Black: {blackCount} (captured Red: {12 - redCount}, Black: {12 - blackCount})");
            if (IsGameOver)
            {
                sb.AppendLine($"GAME OVER - Winner: {Winner}");
            }
            else
            {
                var legal = GetLegalMoves();
                bool mustCapture = legal.Count > 0 && legal[0].IsCapture;
                sb.AppendLine($"Legal moves for {CurrentTurn}{(mustCapture ? " (captures are mandatory)" : "")}: {string.Join(", ", legal.Select(m => m.Pdn))}");
            }
            sb.AppendLine($"Status: {StatusMessage}");
            return sb.ToString();
        }
        #endregion
    }
}
