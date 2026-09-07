// Unit harness for CheckersGame - the English-draughts engine behind CheckersBoard/CheckersApp.
// Compiles the REAL engine source (see CheckersTests.csproj). No mocks.
//
//   dotnet run --project _tools/checkers-tests -c Release
//
// Positions are written with CheckersGame.LoadPosition: 8 rows of 8 chars, row 0 first (Black's back
// rank = the row Red crowns on). '.' empty, r/b men, R/B kings.
using Gemineachy.Games;

int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail != null ? " - " + detail : "")}"); }
}
static string Moves(CheckersGame g) => string.Join(", ", g.GetLegalMoves().Select(m => m.Pdn));
static CheckersGame At(string[] rows, Player turn) { var g = new CheckersGame(); g.LoadPosition(rows, turn); return g; }

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Coordinates (PDN 1-32) ==");
{
    bool roundTrip = true, distinct = true;
    var seen = new HashSet<int>();
    for (int sq = 1; sq <= 32; sq++)
    {
        var (r, c) = CheckersGame.FromSquare(sq);
        if (!CheckersGame.IsDark(r, c) || CheckersGame.ToSquare(r, c) != sq) roundTrip = false;
        if (!seen.Add(r * 8 + c)) distinct = false;
    }
    Check("all 32 squares round-trip through (row,col)", roundTrip);
    Check("all 32 squares map to distinct dark squares", distinct);
    bool lightsAreZero = true;
    for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            if (!CheckersGame.IsDark(r, c) && CheckersGame.ToSquare(r, c) != 0) lightsAreZero = false;
    Check("light squares have no PDN number", lightsAreZero);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Opening position ==");
{
    var g = new CheckersGame();
    int red = 0, black = 0;
    for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            if (g.Board[r, c] == PieceType.Red) red++;
            if (g.Board[r, c] == PieceType.Black) black++;
        }
    Check("12 men per side", red == 12 && black == 12, $"red={red} black={black}");
    Check("Red moves first", g.CurrentTurn == Player.Red);
    // Only the row-5 men can move (row 4 is empty): the man on the a-file has one diagonal,
    // the other three have two => 1 + 2 + 2 + 2 = 7.
    var legal = g.GetLegalMoves();
    Check("7 opening moves", legal.Count == 7, Moves(g));
    Check("no captures at the start", legal.All(m => !m.IsCapture));
    Check("opening moves are written like 22-17", legal.All(m => m.Pdn.Contains('-')));
    // Black's mobility is untouched by Red's first move (Red lands on row 4, Black steps to row 3),
    // so every one of Red's 7 moves leaves Black exactly 7 replies.
    int replies = 0;
    foreach (var m in legal) { var h = new CheckersGame(); h.TryMovePath(m.Path); replies += h.GetLegalMoves().Count; }
    Check("49 positions after one move each", replies == 49, $"got {replies}");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Movement rules ==");
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...r....",
        "........",
        "........",
        "........"], Player.Red);
    var to = g.GetLegalMoves().Select(m => m.To).OrderBy(x => x).ToArray();
    Check("a man moves only forward", to.SequenceEqual(new[] { 14, 15 }), Moves(g));
}
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...R....",
        "........",
        "........",
        "........"], Player.Red);
    var to = g.GetLegalMoves().Select(m => m.To).OrderBy(x => x).ToArray();
    Check("a king moves both ways", to.SequenceEqual(new[] { 14, 15, 22, 23 }), Moves(g));
}
{
    var g = At([
        "........",
        "..r.....",
        "........",
        "........",
        "........",
        "........",
        "........",
        "........"], Player.Red);
    g.TryMove(6, 2);
    Check("a man crowns on the far rank", g.Board[0, 3] == PieceType.RedKing);
}
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "........",
        "........",
        ".....b..",
        "........"], Player.Black);
    g.TryMove(27, 32);
    Check("Black crowns on row 7", g.Board[7, 6] == PieceType.BlackKing, Moves(g));
}
{
    // A piece on a light square would be unreachable and its PDN number aliases a dark neighbour's.
    var threw = false;
    try { At(["........", "........", "........", "........", "..r.....", "........", "........", "........"], Player.Red); }
    catch (ArgumentException) { threw = true; }
    Check("a piece on a light square is rejected", threw);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Captures ==");
{
    // Two Red men: one can jump, one can only step. Captures are mandatory, so only the jump is legal.
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...b....",
        "..r...r.",
        "........",
        "........"], Player.Red);
    var legal = g.GetLegalMoves();
    Check("capture is mandatory - simple moves suppressed", legal.Count == 1, Moves(g));
    Check("capture is written like 22x15", legal[0].Pdn == "22x15", legal[0].Pdn);
    Check("status line announces the forced capture", g.StatusMessage.Contains("must be taken"), g.StatusMessage);
    g.TryMovePath(legal[0].Path);
    Check("the jumped man is removed", g.Board[4, 3] == PieceType.None);
    Check("the jumper lands on the far square", g.Board[3, 4] == PieceType.Red);
    Check("turn passes after a capture", g.CurrentTurn == Player.Black);
}
{
    // A chain of two jumps is ONE move.
    var g = At([
        "........",
        "........",
        "...b....",
        "........",
        "...b....",
        "..r.....",
        "........",
        "........"], Player.Red);
    var legal = g.GetLegalMoves();
    Check("double jump is a single move", legal.Count == 1 && legal[0].Captured.Count == 2, Moves(g));
    Check("double jump writes its whole path", legal[0].Pdn == "22x15x6", legal[0].Pdn);
    g.TryMovePath(legal[0].Path);
    Check("both jumped men are removed", g.Board[4, 3] == PieceType.None && g.Board[2, 3] == PieceType.None);
    Check("the jumper ends on the last square", g.Board[1, 2] == PieceType.Red);
}
{
    // Crowning ENDS the move: the man stops on the king row even though a king could jump on.
    var g = At([
        "........",
        "..b.b...",
        ".....r..",
        "........",
        "........",
        "........",
        "........",
        "........"], Player.Red);
    var legal = g.GetLegalMoves();
    Check("jump onto the king row stops there", legal.Count == 1 && legal[0].Captured.Count == 1, Moves(g));
    g.TryMovePath(legal[0].Path);
    Check("and the man is crowned", g.Board[0, 3] == PieceType.RedKing);
    Check("the other man is untouched", g.Board[1, 2] == PieceType.Black);
    Check("turn passes", g.CurrentTurn == Player.Black);
}
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...R....",
        "....b...",
        "........",
        "........"], Player.Red);
    var legal = g.GetLegalMoves();
    Check("a king captures backward", legal.Count == 1 && legal[0].To == 27, Moves(g));
}
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...r....",
        "....r...",
        "........",
        "........"], Player.Red);
    Check("a piece cannot jump its own colour", g.GetLegalMoves().All(m => !m.IsCapture), Moves(g));
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Game end ==");
{
    var g = At([
        "........",
        "........",
        "........",
        "........",
        "...r....",
        "........",
        "........",
        "........"], Player.Black);
    Check("side with no pieces loses", g.IsGameOver && g.Winner == Player.Red, g.StatusMessage);
}
{
    // Black's lone man is walled in: both steps blocked, and the only jump has no landing square.
    var g = At([
        ".b......",
        "r.r.....",
        "...r....",
        "........",
        "........",
        "........",
        "........",
        "........"], Player.Black);
    Check("side with no legal move loses", g.IsGameOver && g.Winner == Player.Red, $"{g.StatusMessage} | {Moves(g)}");
    Check("result tag records the winner", g.PdnTags["Result"] == "1-0", g.PdnTags["Result"]);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== Move application & bookkeeping ==");
{
    var g = new CheckersGame();
    Check("an illegal move is refused", !g.TryMove(22, 19));
    Check("a refused move does not pass the turn", g.CurrentTurn == Player.Red);
    Check("a refused move does not advance the ply", g.Ply == 0);
    Check("a legal move is accepted", g.TryMove(22, 18));
    Check("ply advances", g.Ply == 1);
    Check("history records the move", g.MoveHistory.Count == 1 && g.MoveHistory[0].Contains("22-18"), string.Join("|", g.MoveHistory));
    g.TryMove(11, 15);
    Check("both colours share a numbered history line", g.MoveHistory.Count == 1 && g.MoveHistory[0].Contains("11-15"), string.Join("|", g.MoveHistory));
    Check("PDN export carries the tags and moves", g.GetPdnFormat().Contains("[Event") && g.GetPdnFormat().Contains("22-18"));
}
{
    var g = new CheckersGame();
    Check("a move refused after game over", At([
        "........", "........", "........", "........",
        "...r....", "........", "........", "........"], Player.Black).TryMove(1, 5) == false);
    var text = g.GetBoardStateText();
    Check("board text states the turn", text.Contains("Turn: Red"));
    Check("board text counts the pieces", text.Contains("Red: 12, Black: 12"));
    // This list is what the agent moves from - if it is ever wrong or absent, Gemini plays illegally.
    Check("board text lists every legal move", g.GetLegalMoves().All(m => text.Contains(m.Pdn)), text);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"== {pass}/{pass + fail} passed, {fail} failed ==");
return fail == 0 ? 0 : 1;
