using System;
using System.Collections.Generic;
using System.Text;

namespace Gemineachy.Games
{
    public enum PieceType { None, Red, Black, RedKing, BlackKing }

    public class CheckersGame
    {
        public PieceType[,] Board { get; private set; } = new PieceType[8, 8];
        public PieceType CurrentTurn { get; private set; } = PieceType.Red;
        public (int Row, int Col)? SelectedSquare { get; private set; }
        public string StatusMessage { get; private set; } = "Red's turn to move.";

        // PDN Meta tags and Move History list
        public Dictionary<string, string> PdnTags { get; private set; } = new();
        public List<string> MoveHistory { get; private set; } = new();
        private int _movePairCounter = 1;

        public CheckersGame()
        {
            InitializeBoard();
        }
        public event Action? OnStateChanged;
        public void InitializeBoard(string eventName = "Casual Text Match", string redName = "Player Red", string blackName = "Player Black")
        {
            // Reset metadata tags
            PdnTags.Clear();
            PdnTags["Event"] = eventName;
            PdnTags["Date"] = DateTime.Now.ToString("yyyy.MM.dd");
            PdnTags["Black"] = blackName;
            PdnTags["Red"] = redName;
            PdnTags["Result"] = "*"; // '*' means in progress

            MoveHistory.Clear();
            _movePairCounter = 1;

            // Clear board 
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    Board[r, c] = PieceType.None;

            // Setup Black pieces on rows 0-2 (dark squares only) 
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 8; c++)
                    if ((r + c) % 2 == 1) Board[r, c] = PieceType.Black;

            // Setup Red pieces on rows 5-7 (dark squares only) 
            for (int r = 5; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    if ((r + c) % 2 == 1) Board[r, c] = PieceType.Red;

            CurrentTurn = PieceType.Red;
            SelectedSquare = null;
            StatusMessage = "Red's turn to move.";
            OnStateChanged?.Invoke();
        }

        public void SelectSquareAndMove(int row, int col, int rowDest, int colDest)
        {
            SelectedSquare = null;
            SelectSquare(row, col);
            SelectSquare(rowDest, colDest);
        }

        public void SelectSquare(int row, int col)
        {
            var piece = Board[row, col];

            if (IsCurrentPlayersPiece(piece))
            {
                SelectedSquare = (row, col);
                StatusMessage = $"Selected piece at ({row}, {col}). Choose a destination.";
                return;
            }

            if (SelectedSquare != null && piece == PieceType.None && (row + col) % 2 == 1)
            {
                var (sRow, sCol) = SelectedSquare.Value;

                int rowDiff = row - sRow;
                int colDiff = Math.Abs(col - sCol);
                bool isCorrectDirection = CurrentTurn == PieceType.Red ? rowDiff == -1 : rowDiff == 1;

                var selectedPiece = Board[sRow, sCol];
                if (selectedPiece == PieceType.RedKing || selectedPiece == PieceType.BlackKing)
                {
                    isCorrectDirection = Math.Abs(rowDiff) == 1;
                }

                if (isCorrectDirection && colDiff == 1)
                {
                    // 1. Generate the move string using standard 1-32 checkers notation
                    string pdnMove = ConvertToPdnNotation(sRow, sCol, row, col, isCapture: false);
                    RecordMove(pdnMove);

                    // 2. Execute move 
                    Board[row, col] = selectedPiece;
                    Board[sRow, sCol] = PieceType.None;

                    // Check for king promotion 
                    if (CurrentTurn == PieceType.Red && row == 0) Board[row, col] = PieceType.RedKing;
                    if (CurrentTurn == PieceType.Black && row == 7) Board[row, col] = PieceType.BlackKing;

                    // Switch turn 
                    CurrentTurn = CurrentTurn == PieceType.Red ? PieceType.Black : PieceType.Red;
                    SelectedSquare = null;
                    StatusMessage = $"{CurrentTurn}'s turn to move.";

                    OnStateChanged?.Invoke();
                }
                else
                {
                    StatusMessage = "Invalid move. Try again.";
                }
            }
        }

        private bool IsCurrentPlayersPiece(PieceType piece)
        {
            if (CurrentTurn == PieceType.Red) return piece == PieceType.Red || piece == PieceType.RedKing;
            return piece == PieceType.Black || piece == PieceType.BlackKing;
        }

        /// <summary>
        /// Combines moves sequentially. Black always records first in a PDN pair.
        /// </summary>
        private void RecordMove(string pdnMove)
        {
            if (CurrentTurn == PieceType.Black)
            {
                // Black starts the turn pair number (e.g., "1. 11-15")
                MoveHistory.Add($"{_movePairCounter}. {pdnMove}");
            }
            else
            {
                // Red appends to the last turn pair, or starts if it's the very first turn of the game
                if (MoveHistory.Count == 0)
                {
                    MoveHistory.Add($"1. ... {pdnMove}");
                }
                else
                {
                    MoveHistory[MoveHistory.Count - 1] += $" {pdnMove}";
                }
                _movePairCounter++;
            }
        }

        /// <summary>
        /// Maps an 8x8 2D matrix coordinate to official 1-32 Checkers Square Numbers.
        /// Returns empty string if the coordinate lands on an invalid white square.
        /// </summary>
        private string ConvertToPdnNotation(int startRow, int startCol, int endRow, int endCol, bool isCapture)
        {
            int startSquare = GetCheckersSquareNumber(startRow, startCol);
            int endSquare = GetCheckersSquareNumber(endRow, endCol);

            char separator = isCapture ? 'x' : '-';
            return $"{startSquare}{separator}{endSquare}";
        }

        private int GetCheckersSquareNumber(int row, int col)
        {
            if ((row + col) % 2 == 0) return 0; // White square (not used)
            return (row * 4) + (col / 2) + 1; // Maps 0-7 matrix to 1-32 system
        }

        /// <summary>
        /// Outputs the entire game state and full history as an official, valid PDN string.
        /// </summary>
        public string GetPdnFormat()
        {
            var sb = new StringBuilder();

            // Print Tag Pairs
            foreach (var tag in PdnTags)
            {
                sb.AppendLine($"[{tag.Key} \"{tag.Value}\"]");
            }
            sb.AppendLine();

            // Print Movetext
            sb.AppendLine(string.Join(" ", MoveHistory));

            return sb.ToString();
        }


        /// <summary>
        /// Generates a clean, text-based 2D board matrix with accompanying game context variables.
        /// </summary>
        public string GetBoardStateText()
        {
            var sb = new StringBuilder();

            // 1. Build the 2D Grid Header
            sb.AppendLine("  a b c d e f g h");

            // 2. Build the 8x8 Board Matrix
            for (int r = 0; r < 8; r++)
            {
                // Print row identifier (mapping 0-7 array index to 1-8 display rows)
                sb.Append($"{r + 1} ");

                for (int c = 0; c < 8; c++)
                {
                    char symbol = Board[r, c] switch
                    {
                        PieceType.Black => 'b',
                        PieceType.Red => 'r',
                        PieceType.BlackKing => 'B',
                        PieceType.RedKing => 'R',
                        _ => '.' // PieceType.None
                    };

                    sb.Append($"{symbol} ");
                }
                sb.AppendLine();
            }

            // 3. Calculate Captured Pieces (Total starting pieces per color is 12)
            int redCount = 0;
            int blackCount = 0;

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    if (Board[r, c] == PieceType.Red || Board[r, c] == PieceType.RedKing) redCount++;
                    if (Board[r, c] == PieceType.Black || Board[r, c] == PieceType.BlackKing) blackCount++;
                }
            }

            int redCaptured = 12 - redCount;
            int blackCaptured = 12 - blackCount;

            // 4. Append Crucial Game Context Variables
            sb.AppendLine();
            sb.AppendLine($"Turn: {CurrentTurn}");
            sb.AppendLine($"Captured: R:{redCaptured}, B:{blackCaptured}");
            sb.AppendLine($"Status: {StatusMessage}");

            return sb.ToString();
        }
    }
}
