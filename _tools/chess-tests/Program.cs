using Gemineachy.Games;

int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail != null ? " - " + detail : "")}"); }
}
void Perft(string name, string? fen, int depth, long expected)
{
    var g = new ChessGame();
    if (fen != null) g.LoadFen(fen);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long n = g.Perft(depth);
    sw.Stop();
    Check($"{name} perft({depth})={expected}", n == expected, $"got {n} in {sw.ElapsedMilliseconds}ms");
}

Console.WriteLine("== Perft (move-generation correctness) ==");
// Startpos
Perft("startpos", null, 1, 20);
Perft("startpos", null, 2, 400);
Perft("startpos", null, 3, 8902);
Perft("startpos", null, 4, 197281);
// Kiwipete - castling, en passant, checks, pins
const string kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -";
Perft("kiwipete", kiwipete, 1, 48);
Perft("kiwipete", kiwipete, 2, 2039);
Perft("kiwipete", kiwipete, 3, 97862);
// Position 3 - promotions, ep, checks
const string pos3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - -";
Perft("pos3", pos3, 1, 14);
Perft("pos3", pos3, 2, 191);
Perft("pos3", pos3, 3, 2812);
Perft("pos3", pos3, 4, 43238);
// Position 4 - lots of promotions/captures
const string pos4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq -";
Perft("pos4", pos4, 1, 6);
Perft("pos4", pos4, 2, 264);
Perft("pos4", pos4, 3, 9467);
// Position 5
const string pos5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ -";
Perft("pos5", pos5, 1, 44);
Perft("pos5", pos5, 2, 1486);
Perft("pos5", pos5, 3, 62379);

Console.WriteLine();
Console.WriteLine("== Rules / adjudication ==");

// Fool's mate: 1. f3 e5 2. g4 Qh4#
{
    var g = new ChessGame();
    bool a = g.TryMoveUci("f2f3");
    bool b = g.TryMoveUci("e7e5");
    bool c = g.TryMoveUci("g2g4");
    bool d = g.TryMoveUci("d8h4");
    Check("fool's mate: all 4 moves legal", a && b && c && d);
    Check("fool's mate: game over", g.IsGameOver);
    Check("fool's mate: Black wins by checkmate", g.Winner == ChessColor.Black && (g.Outcome ?? "").Contains("Checkmate"));
}

// Stalemate: Black to move, Kh8, White Qf7 + Kg6 - no legal moves, not in check.
{
    var g = new ChessGame();
    g.LoadFen("7k/5Q2/6K1/8/8/8/8/8 b - -");
    Check("stalemate: game over", g.IsGameOver);
    Check("stalemate: draw (no winner) + Outcome says stalemate", g.Winner == null && (g.Outcome ?? "").Contains("Stalemate"));
}

// Castling moves present (Kiwipete: white may castle both sides).
{
    var g = new ChessGame();
    g.LoadFen(kiwipete);
    Check("castling: e1g1 (O-O) legal", g.MatchUci("e1g1") != null);
    Check("castling: e1c1 (O-O-O) legal", g.MatchUci("e1c1") != null);
}

// Promotion: white pawn a7 promotes to all four pieces.
{
    var g = new ChessGame();
    g.LoadFen("8/P7/8/8/8/8/8/k6K w - -");
    Check("promotion: a7a8q legal", g.MatchUci("a7a8q") != null);
    Check("promotion: a7a8n legal", g.MatchUci("a7a8n") != null);
    var promos = g.GetLegalMoves().Count(m => m.FromR == 1 && m.FromC == 0 && m.Promotion != PieceKind.None);
    Check("promotion: exactly 4 promo moves for a7 pawn", promos == 4, $"got {promos}");
}

// Promotion picker engine path: IsPromotionMove detection + explicit under-promotion via TryMove.
{
    var g = new ChessGame();
    g.LoadFen("8/P7/8/8/8/8/8/k6K w - -"); // white pawn a7 (row1,col0) -> a8 (row0,col0)
    Check("picker: IsPromotionMove(a7->a8) true", g.IsPromotionMove(1, 0, 0, 0));
    Check("picker: IsPromotionMove(non-promo king move) false", !g.IsPromotionMove(7, 7, 6, 7));
    bool moved = g.TryMove(1, 0, 0, 0, PieceKind.Knight); // under-promote to knight, as the picker would
    var top = g.Board[0, 0];
    Check("picker: under-promote to Knight applied", moved && top.Kind == PieceKind.Knight && top.Color == ChessColor.White);
}

// En passant capture is generated when the FEN sets an ep target.
{
    var g = new ChessGame();
    g.LoadFen("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6");
    Check("en passant: e5f6 legal", g.MatchUci("e5f6") != null);
}

// Insufficient material: K vs K is an immediate draw when loaded.
{
    var g = new ChessGame();
    g.LoadFen("8/8/8/4k3/8/8/4K3/8 w - -");
    Check("insufficient material: K vs K is a draw", g.IsGameOver && g.Winner == null && (g.Outcome ?? "").Contains("insufficient"));
}

Console.WriteLine();
Console.WriteLine($"== RESULT: {pass} passed, {fail} failed ==");
return fail == 0 ? 0 : 1;
