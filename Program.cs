// =============================================================================
// EscapeTheMaze — A console-based maze game for Visual Studio (.NET)
//
// Architecture overview:
//   All game state and logic live in the static Program class, organised into
//   clearly labelled regions that follow the natural flow of the game:
//
//     Constants → State → Maze Data → Entry Point → UI Screens →
//     Core Game Loop → Rendering → AI / Pathfinding → Persistence → Utilities
//
// Rendering strategy:
//   Console.SetCursorPosition(0,0) is used instead of Console.Clear() to
//   eliminate frame flicker.  The entire frame is built into a StringBuilder
//   and flushed in a single Console.Write call wherever possible.
//
// Difficulty levels:
//   1 – Easy   : no enemy, full visibility
//   2 – Medium : random-walk enemy, fog-of-war radius
//   3 – Hard   : BFS-chasing enemy, fog-of-war radius
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace EscapeTheMaze
{
    /// <summary>
    /// Entry point and sole game controller for Escape The Maze.
    /// All game state is held in static fields; all behaviour is expressed
    /// through clearly separated static methods grouped by responsibility.
    /// </summary>
    internal class Program
    {
        // =====================================================================
        // REGION: Constants
        // Fixed values that never change at runtime.  Centralised here so that
        // tweaking game feel (fog radius, trail length, score file name) requires
        // changing exactly one line.
        // =====================================================================

        /// <summary>Maximum Euclidean distance (in cells) visible through the fog of war.</summary>
        private const double FogOfWarRadius = 5.5;

        /// <summary>Maximum number of visited cells kept in the player trail before the oldest is discarded.</summary>
        private const int MaxTrailLength = 200;

        /// <summary>How many of the most-recently visited trail cells are rendered in the brighter colour.</summary>
        private const int RecentTrailThreshold = 10;

        /// <summary>Path to the plain-text file used to persist the top-ten high scores between sessions.</summary>
        private const string HighScoreFilePath = "highscores.txt";

        /// <summary>Maximum number of entries stored in the high-score file.</summary>
        private const int MaxHighScores = 10;

        /// <summary>Character used in maze strings to mark an impassable wall cell.</summary>
        private const char TileWall = '#';

        /// <summary>Character used in maze strings to mark the exit cell the player must reach.</summary>
        private const char TileExit = 'E';

        /// <summary>Character rendered in place of every wall tile for a cleaner visual appearance.</summary>
        private const char GlyphWall = '█';

        /// <summary>Character rendered on trail cells that were visited recently.</summary>
        private const char GlyphTrail = '·';

        /// <summary>Character used to represent the enemy actor on screen.</summary>
        private const char GlyphEnemy = 'M';

        // =====================================================================
        // REGION: Game State
        // Mutable fields that track the current session.  Each field is named to
        // describe the concept it represents, not the data type it holds.
        // =====================================================================

        /// <summary>Index into <see cref="Mazes"/> identifying which maze is currently being played.</summary>
        private static int currentMazeIndex = 0;

        /// <summary>
        /// Whether audio feedback is enabled.
        /// Toggled from the main-menu sound setting.
        /// </summary>
        private static bool soundEnabled = true;

        /// <summary>Shared random-number generator; reused to avoid repeated seeding artefacts.</summary>
        private static readonly Random Rng = new Random();

        /// <summary>Horizontal position (column) of the player within the current maze grid.</summary>
        private static int playerX;

        /// <summary>Vertical position (row) of the player within the current maze grid.</summary>
        private static int playerY;

        /// <summary>Horizontal position (column) of the enemy actor within the current maze grid.</summary>
        private static int enemyX;

        /// <summary>Vertical position (row) of the enemy actor within the current maze grid.</summary>
        private static int enemyY;

        /// <summary>Whether an enemy is present in the current difficulty setting.</summary>
        private static bool isEnemyActive = false;

        /// <summary>
        /// Whether the enemy uses BFS pathfinding to chase the player (Hard difficulty).
        /// When <see langword="false"/> the enemy performs a random walk (Medium difficulty).
        /// </summary>
        private static bool isEnemySmart = false;

        /// <summary>Running total of steps taken across all mazes in the current session.</summary>
        private static int totalSteps = 0;

        /// <summary>Steps taken within the maze that is currently being played.</summary>
        private static int stepsThisMaze = 0;

        /// <summary>
        /// The character displayed on-screen for the player.
        /// Changes with difficulty so the player has a visual cue for their current setting.
        /// </summary>
        private static char playerGlyph = 'P';

        /// <summary>
        /// Numeric difficulty level selected by the player.
        /// 1 = Easy, 2 = Medium, 3 = Hard.
        /// </summary>
        private static int difficulty = 1;

        /// <summary>
        /// Ordered list of cells visited by the player, used to render the breadcrumb trail.
        /// Oldest entries are pruned when the list exceeds <see cref="MaxTrailLength"/>.
        /// </summary>
        private static readonly List<(int X, int Y)> PlayerTrail = new List<(int X, int Y)>();

        /// <summary>Elapsed-time tracker started at the beginning of each full game session.</summary>
        private static readonly Stopwatch SessionTimer = new Stopwatch();

        // =====================================================================
        // REGION: Maze Data
        // Each maze is a rectangular array of strings.  Legal tile characters:
        //   '#'  — wall (impassable)
        //   ' '  — open corridor
        //   'E'  — exit (goal)
        // Player always starts at cell (1, 1).  All three mazes are verified
        // solvable via BFS from that start position.
        // =====================================================================

        /// <summary>
        /// The ordered sequence of mazes the player must complete to win.
        /// Mazes are presented in increasing order of path length:
        /// Maze 1 ≈ 21 optimal steps, Maze 2 ≈ 47, Maze 3 ≈ 60.
        /// </summary>
        private static readonly List<string[]> Mazes = new List<string[]>
        {
            // Maze 1 — introductory layout, short optimal path (~21 steps)
            new string[]
            {
                "####################",
                "#      #       #   #",
                "# ### ##### ## # # #",
                "#   #     # ##   # #",
                "### ##### # ###### #",
                "#     #   #      # #",
                "# ### # ####### ## #",
                "# #   #       #    #",
                "# # ######### #### #",
                "#         E        #",
                "####################"
            },

            // Maze 2 — branching corridors, medium path (~47 steps).
            // NOTE: the opening at row 4, col 16 is intentional; removing it
            // seals the exit into an unreachable island (verified by BFS).
            new string[]
            {
                "####################",
                "#          #      E#",
                "#  ##### # # #######",
                "##     # # #       #",
                "###### # # ##### # #",
                "#      # #       # #",
                "# ###### ####### # #",
                "#                # #",
                "################## #",
                "#                  #",
                "####################"
            },

            // Maze 3 — spiral layout, longest optimal path (~60 steps)
            new string[]
            {
                "####################",
                "#                  #",
                "################## #",
                "#                # #",
                "# ############## # #",
                "# #            # # #",
                "# # ########## # # #",
                "# #          # #   #",
                "# ########## # #####",
                "#          E       #",
                "####################"
            }
        };

        // =====================================================================
        // REGION: Entry Point & Console Initialisation
        // =====================================================================

        /// <summary>
        /// Application entry point.
        /// Configures the console window once and then hands control to the
        /// main-menu loop, which runs for the lifetime of the process.
        /// </summary>
        /// <param name="args">Command-line arguments (unused).</param>
        private static void Main(string[] args)
        {
            InitialiseConsole();
            ShowMainMenu();
        }

        /// <summary>
        /// Configures the console window and buffer to fit the widest maze
        /// without triggering scroll bars, which would cause visual tearing.
        /// Wrapped in a try/catch because some platforms (e.g. redirected output)
        /// do not support window resizing and throw an <see cref="IOException"/>.
        /// </summary>
        private static void InitialiseConsole()
        {
            try
            {
                int requiredWidth = Mazes[0][0].Length + 14;
                int requiredHeight = Mazes[0].Length + 10;

                if (Console.WindowWidth < requiredWidth) Console.WindowWidth = requiredWidth;
                if (Console.WindowHeight < requiredHeight) Console.WindowHeight = requiredHeight + 4;

                // Keep the buffer the same size as the window so the scroll bar never appears.
                Console.BufferWidth = Console.WindowWidth;
                Console.BufferHeight = Console.WindowHeight;
            }
            catch { /* Resizing is unsupported on this platform; continue with defaults. */ }

            Console.CursorVisible = false;
            Console.Title = "Escape The Maze";
            Console.OutputEncoding = Encoding.UTF8;
        }

        // =====================================================================
        // REGION: UI Screens
        // Each screen is a self-contained method that builds its frame array,
        // calls DrawFrame, reads one input, and either loops or delegates.
        // =====================================================================

        /// <summary>
        /// Displays the main menu and dispatches to sub-screens based on player input.
        /// Loops indefinitely until the player chooses to exit the application.
        /// </summary>
        private static void ShowMainMenu()
        {
            while (true)
            {
                Console.Clear();
                Console.CursorVisible = false;

                string[] frame =
                {
                    "",
                    "  ╔══════════════════════════════════╗",
                    "  ║         ESCAPE  THE  MAZE        ║",
                    "  ╚══════════════════════════════════╝",
                    "",
                    "  1.  Start Game",
                    "  2.  View High Scores",
                    "  3.  Sound  [ " + (soundEnabled ? "ON " : "OFF") + " ]",
                    "  4.  Exit",
                    "",
                    "  Select Option: "
                };

                DrawFrame(frame);
                Console.SetCursorPosition(18, 10);
                Console.CursorVisible = true;
                string choice = Console.ReadLine();
                Console.CursorVisible = false;

                switch (choice?.Trim())
                {
                    case "1": SelectDifficulty(); break;
                    case "2": ShowHighScores(); break;
                    case "3": ToggleSound(); break;
                    case "4": Environment.Exit(0); break;
                }
            }
        }

        /// <summary>
        /// Prompts the player to choose a difficulty level and configures the
        /// corresponding enemy behaviour and player glyph before starting the game.
        /// Loops until a valid choice is made so invalid input is silently ignored.
        /// </summary>
        private static void SelectDifficulty()
        {
            while (true)
            {
                Console.Clear();

                string[] frame =
                {
                    "",
                    "  ╔══════════════════════════════════╗",
                    "  ║         CHOOSE  DIFFICULTY       ║",
                    "  ╚══════════════════════════════════╝",
                    "",
                    "  1.  Easy    — No enemy, full visibility",
                    "  2.  Medium  — Random enemy, fog of war",
                    "  3.  Hard    — Chasing enemy, fog of war",
                    "",
                    "  Choice: "
                };

                DrawFrame(frame);
                Console.SetCursorPosition(10, 9);
                Console.CursorVisible = true;
                string choice = Console.ReadLine();
                Console.CursorVisible = false;

                switch (choice?.Trim())
                {
                    case "1":
                        difficulty = 1;
                        playerGlyph = 'P';
                        isEnemyActive = false;
                        isEnemySmart = false;
                        PlayCountdown();
                        StartGame();
                        return;

                    case "2":
                        difficulty = 2;
                        playerGlyph = '@';
                        isEnemyActive = true;
                        isEnemySmart = false;
                        PlayCountdown();
                        StartGame();
                        return;

                    case "3":
                        difficulty = 3;
                        playerGlyph = 'X';
                        isEnemyActive = true;
                        isEnemySmart = true;
                        PlayCountdown();
                        StartGame();
                        return;

                    default:
                        // Invalid input — loop silently to re-display the menu.
                        break;
                }
            }
        }

        /// <summary>
        /// Displays a 3-2-1 countdown with an ascending beep sequence to give
        /// the player a moment to focus before the first maze appears.
        /// </summary>
        private static void PlayCountdown()
        {
            // Count down from 3, raising pitch with each beat to build anticipation.
            for (int count = 3; count >= 1; count--)
            {
                string[] frame =
                {
                    "",
                    "  Starting in...",
                    "",
                    "        " + count,
                    ""
                };

                DrawFrame(frame);
                PlayBeep(800 + count * 100, 120);
                Thread.Sleep(900);
            }

            string[] goFrame = { "", "         GO!", "" };
            DrawFrame(goFrame);
            PlayBeep(1400, 200);
            Thread.Sleep(400);
        }

        /// <summary>
        /// Displays the post-game menu that appears after a session ends
        /// (either by completing all mazes or being caught by the enemy).
        /// Loops until the player makes a valid choice.
        /// </summary>
        private static void ShowEndGameMenu()
        {
            while (true)
            {
                string[] frame =
                {
                    "",
                    "  ╔══════════════════════════════════╗",
                    "  ║            GAME  OVER            ║",
                    "  ╚══════════════════════════════════╝",
                    "",
                    "  1.  Play Again",
                    "  2.  View High Scores",
                    "  3.  Main Menu",
                    "  4.  Exit",
                    "",
                    "  Choice: "
                };

                DrawFrame(frame);
                Console.SetCursorPosition(10, 10);
                Console.CursorVisible = true;
                string choice = Console.ReadLine();
                Console.CursorVisible = false;

                switch (choice?.Trim())
                {
                    case "1": StartGame(); return;
                    case "2": ShowHighScores(); break;
                    case "3": return;
                    case "4": Environment.Exit(0); break;
                }
            }
        }

        /// <summary>
        /// Displays the pause overlay and suspends the session timer.
        /// The timer resumes the instant the player presses any key,
        /// ensuring pausing cannot be exploited to improve the time score.
        /// </summary>
        private static void PauseGame()
        {
            SessionTimer.Stop();

            string[] frame =
            {
                "",
                "  ╔══════════════════════════════════╗",
                "  ║             PAUSED               ║",
                "  ╚══════════════════════════════════╝",
                "",
                "  Press any key to resume...",
                ""
            };

            DrawFrame(frame);
            Console.ReadKey(true);

            SessionTimer.Start();
        }

        /// <summary>
        /// Reads the high-score file and displays the stored entries, ranked
        /// by step count ascending (fewer steps = better score).
        /// Shows a friendly message when no scores have been saved yet.
        /// </summary>
        private static void ShowHighScores()
        {
            Console.Clear();

            var lines = new List<string>
            {
                "",
                "  ╔══════════════════════════════════════════╗",
                "  ║              HIGH  SCORES                ║",
                "  ╚══════════════════════════════════════════╝",
                ""
            };

            if (!File.Exists(HighScoreFilePath))
            {
                lines.Add("  No scores yet — be the first!");
            }
            else
            {
                string[] storedScores = File.ReadAllLines(HighScoreFilePath);

                for (int rank = 0; rank < storedScores.Length; rank++)
                {
                    // Each line is stored as "paddedSteps|displayText"; extract the human-readable part.
                    string displayText = storedScores[rank].Contains("|")
                        ? storedScores[rank].Split('|')[1]
                        : storedScores[rank];

                    lines.Add("  " + (rank + 1) + ".  " + displayText);
                }
            }

            lines.Add("");
            lines.Add("  Press any key to return.");
            lines.Add("");

            DrawFrame(lines.ToArray());
            Console.ReadKey(true);
        }

        /// <summary>
        /// Toggles audio on or off and gives immediate audible confirmation
        /// when sound is turned back on (so the player knows it worked).
        /// </summary>
        private static void ToggleSound()
        {
            soundEnabled = !soundEnabled;

            string[] frame =
            {
                "",
                "  Sound is now: " + (soundEnabled ? "ON" : "OFF"),
                "",
                "  Press any key...",
                ""
            };

            DrawFrame(frame);

            // Play a confirmation beep only when sound has just been enabled.
            if (soundEnabled)
                PlayBeep(1000, 200);

            Console.ReadKey(true);
        }

        // =====================================================================
        // REGION: Core Game Loop
        // StartGame orchestrates the session; PlayMaze handles one level.
        // =====================================================================

        /// <summary>
        /// Resets session state and iterates through every maze in sequence.
        /// Saves the score and shows the victory screen on full completion,
        /// or shows the game-over screen if the player is caught.
        /// </summary>
        private static void StartGame()
        {
            currentMazeIndex = 0;
            totalSteps = 0;
            SessionTimer.Restart();

            bool sessionWon = true;

            while (currentMazeIndex < Mazes.Count)
            {
                bool mazeCompleted = PlayMaze(Mazes[currentMazeIndex]);

                if (!mazeCompleted)
                {
                    sessionWon = false;
                    break;
                }

                currentMazeIndex++;
            }

            SessionTimer.Stop();

            if (sessionWon)
            {
                SaveScore(totalSteps, SessionTimer.Elapsed);

                string elapsedFormatted = FormatElapsedTime(SessionTimer.Elapsed);

                string[] victoryFrame =
                {
                    "",
                    "  ╔══════════════════════════════════╗",
                    "  ║        ALL  MAZES  COMPLETE!     ║",
                    "  ╚══════════════════════════════════╝",
                    "",
                    "  Total Steps : " + totalSteps,
                    "  Time        : " + elapsedFormatted,
                    "",
                    "  Score saved!",
                    ""
                };

                DrawFrame(victoryFrame);

                // Ascending three-note fanfare to reward completion.
                PlayBeep(1500, 200);
                Thread.Sleep(100);
                PlayBeep(1800, 200);
                Thread.Sleep(100);
                PlayBeep(2000, 400);
                Thread.Sleep(1500);

                ShowEndGameMenu();
            }
            else
            {
                Console.Clear();

                string[] gameOverFrame =
                {
                    "",
                    "  ╔══════════════════════════════════╗",
                    "  ║         GAME  OVER               ║",
                    "  ╚══════════════════════════════════╝",
                    "",
                    "  The enemy caught you!",
                    "  Steps so far: " + totalSteps,
                    "",
                    "  Press any key...",
                    ""
                };

                DrawFrame(gameOverFrame);
                PlayBeep(150, 600);
                Console.ReadKey(true);
                ShowEndGameMenu();
            }
        }

        /// <summary>
        /// Runs the interactive game loop for a single maze level.
        /// Handles player input, movement validation, exit detection,
        /// enemy movement, and collision checks each frame.
        /// </summary>
        /// <param name="maze">The maze grid to play, as an array of equal-length strings.</param>
        /// <returns>
        /// <see langword="true"/> if the player reached the exit;
        /// <see langword="false"/> if the player was caught by the enemy or pressed Escape.
        /// </returns>
        private static bool PlayMaze(string[] maze)
        {
            // Initialise player at the standard top-left open cell.
            playerX = 1;
            playerY = 1;
            stepsThisMaze = 0;
            PlayerTrail.Clear();

            // Place the enemy at the bottom-right corner, walking left until an open cell is found.
            if (isEnemyActive)
            {
                enemyX = maze[0].Length - 2;
                enemyY = maze.Length - 2;

                while (maze[enemyY][enemyX] == TileWall && enemyX > 1)
                    enemyX--;
            }

            while (true)
            {
                DrawMaze(maze);

                ConsoleKeyInfo keyPress = Console.ReadKey(true);

                int targetX = playerX;
                int targetY = playerY;

                switch (keyPress.Key)
                {
                    case ConsoleKey.W: case ConsoleKey.UpArrow: targetY--; break;
                    case ConsoleKey.S: case ConsoleKey.DownArrow: targetY++; break;
                    case ConsoleKey.A: case ConsoleKey.LeftArrow: targetX--; break;
                    case ConsoleKey.D: case ConsoleKey.RightArrow: targetX++; break;
                    case ConsoleKey.P: PauseGame(); continue;
                    case ConsoleKey.H: ShowHint(maze); continue;
                    case ConsoleKey.Escape: return false;
                }

                // Discard moves that would step outside the maze array bounds.
                if (targetY < 0 || targetY >= maze.Length ||
                    targetX < 0 || targetX >= maze[targetY].Length)
                    continue;

                char targetTile = maze[targetY][targetX];

                // --- Exit reached ---
                if (targetTile == TileExit)
                {
                    PlayerTrail.Add((playerX, playerY));
                    playerX = targetX;
                    playerY = targetY;
                    stepsThisMaze++;
                    totalSteps++;

                    // Brief pause so the player sees the exit cell highlighted before the screen changes.
                    DrawMaze(maze);
                    Thread.Sleep(300);

                    Console.Clear();

                    string[] mazeCompleteFrame =
                    {
                        "",
                        "  ╔══════════════════════════════════╗",
                        "  ║         MAZE  " + (currentMazeIndex + 1) + "  COMPLETE!       ║",
                        "  ╚══════════════════════════════════╝",
                        "",
                        "  Steps this maze : " + stepsThisMaze,
                        "  Total steps     : " + totalSteps,
                        "",
                        "  Press any key for next maze...",
                        ""
                    };

                    DrawFrame(mazeCompleteFrame);

                    // Ascending three-note chime for level completion.
                    PlayBeep(1200, 150);
                    Thread.Sleep(100);
                    PlayBeep(1500, 150);
                    Thread.Sleep(100);
                    PlayBeep(1800, 300);

                    Console.ReadKey(true);
                    return true;
                }

                // --- Normal movement ---
                if (targetTile != TileWall)
                {
                    PlayerTrail.Add((playerX, playerY));

                    // Prune the oldest trail entry to keep memory use bounded.
                    if (PlayerTrail.Count > MaxTrailLength)
                        PlayerTrail.RemoveAt(0);

                    playerX = targetX;
                    playerY = targetY;
                    stepsThisMaze++;
                    totalSteps++;
                    PlayBeep(650, 30);
                }
                else
                {
                    // Low-pitched bump sound signals an impassable wall.
                    PlayBeep(200, 60);
                }

                // --- Enemy turn ---
                if (isEnemyActive)
                {
                    AdvanceEnemy(maze);

                    // Evaluate catch condition after the enemy has moved so the
                    // player always has a chance to react before being caught.
                    if (playerX == enemyX && playerY == enemyY)
                        return false;
                }
            }
        }

        // =====================================================================
        // REGION: Rendering
        // DrawMaze and DrawFrame are the only two methods that write to the
        // console during gameplay.  Both use cursor repositioning rather than
        // Console.Clear() to avoid the flicker caused by screen erasure.
        // =====================================================================

        /// <summary>
        /// Renders the current maze state — HUD header, all visible cells, and
        /// a blank footer — in a single pass to the console without clearing.
        /// <para>
        /// Each cell is categorised in priority order:
        /// player → enemy → exit → wall → trail → open space.
        /// Cells outside the fog-of-war radius are replaced with a blank space
        /// on difficulty 2 and 3 to limit player visibility.
        /// </para>
        /// </summary>
        /// <param name="maze">The maze grid being rendered.</param>
        private static void DrawMaze(string[] maze)
        {
            int mazeHeight = maze.Length;

            // Build the HUD header line into a StringBuilder so it is written as
            // one string rather than multiple concatenated Console.Write calls.
            var header = new StringBuilder();
            header.AppendLine(
                "  Maze " + (currentMazeIndex + 1) + "/" + Mazes.Count
                + "   Steps: " + stepsThisMaze
                + "   Total: " + totalSteps
                + "   Time: " + FormatElapsedTime(SessionTimer.Elapsed)
                + "   [WASD/Arrows=Move  P=Pause  H=Hint  ESC=Quit]");
            header.AppendLine();

            // Collect every cell as a (glyph, foreground colour) pair.
            // Grouping by row allows a single SetCursorPosition call per line.
            var renderRows = new List<List<(string Glyph, ConsoleColor Foreground)>>();

            for (int row = 0; row < mazeHeight; row++)
            {
                var cellsInRow = new List<(string, ConsoleColor)>();

                // Two-space left indent keeps the maze away from the window edge.
                cellsInRow.Add(("  ", ConsoleColor.Gray));

                for (int col = 0; col < maze[row].Length; col++)
                {
                    // Fog of war: compute Euclidean distance from player.
                    // On Easy (difficulty 1) distance is forced to 0 so everything is visible.
                    double distanceFromPlayer = difficulty == 1
                        ? 0
                        : Math.Sqrt(Math.Pow(playerX - col, 2) + Math.Pow(playerY - row, 2));

                    if (difficulty > 1 && distanceFromPlayer > FogOfWarRadius)
                    {
                        cellsInRow.Add((" ", ConsoleColor.DarkGray));
                        continue;
                    }

                    // Priority rendering: player overrides all other tile types.
                    if (col == playerX && row == playerY)
                    {
                        cellsInRow.Add((playerGlyph.ToString(), ConsoleColor.Green));
                    }
                    else if (isEnemyActive && col == enemyX && row == enemyY)
                    {
                        cellsInRow.Add((GlyphEnemy.ToString(), ConsoleColor.Red));
                    }
                    else if (maze[row][col] == TileExit)
                    {
                        // Pulse the exit between two yellows to draw the eye without being distracting.
                        bool pulseOn = (DateTime.Now.Millisecond / 300) % 2 == 0;
                        cellsInRow.Add((TileExit.ToString(), pulseOn ? ConsoleColor.Yellow : ConsoleColor.DarkYellow));
                    }
                    else if (maze[row][col] == TileWall)
                    {
                        cellsInRow.Add((GlyphWall.ToString(), ConsoleColor.DarkCyan));
                    }
                    else if (PlayerTrail.Contains((col, row)))
                    {
                        // Recent trail cells (near the end of the list) render brighter than older ones.
                        int trailIndex = PlayerTrail.IndexOf((col, row));
                        bool isRecent = trailIndex >= PlayerTrail.Count - RecentTrailThreshold;
                        cellsInRow.Add((GlyphTrail.ToString(), isRecent ? ConsoleColor.Gray : ConsoleColor.DarkGray));
                    }
                    else
                    {
                        cellsInRow.Add((" ", ConsoleColor.Gray));
                    }
                }

                renderRows.Add(cellsInRow);
            }

            // --- Output phase ---
            // Reposition to the top-left corner and write the header, padded to
            // the full window width so any stale header text is always overwritten.
            Console.SetCursorPosition(0, 0);
            Console.ForegroundColor = ConsoleColor.DarkGray;

            // The header StringBuilder ends with newlines; strip them so PadRight
            // measures the actual content width, not the width plus trailing lines.
            string headerLine = header.ToString().TrimEnd('\r', '\n');
            Console.WriteLine(headerLine.PadRight(Console.WindowWidth - 1));
            Console.WriteLine(new string(' ', Console.WindowWidth - 1));

            // Prepare a reusable blank string for end-of-row and footer padding.
            string fullBlankLine = new string(' ', Console.WindowWidth - 1);

            // Write each maze row with per-cell colours, then pad the remainder of
            // the console line with spaces so stale characters to the right are erased.
            foreach (var cellRow in renderRows)
            {
                // Count characters written on this line so the correct number of
                // trailing spaces can be appended to reach the window edge.
                int charsWritten = 0;

                foreach (var (glyph, foreground) in cellRow)
                {
                    Console.ForegroundColor = foreground;
                    Console.Write(glyph);
                    charsWritten += glyph.Length;
                }

                // Overwrite the rest of the console line with spaces, then move to
                // the next line explicitly so the newline never skips the padding.
                Console.ResetColor();
                int remainingColumns = Console.WindowWidth - 1 - charsWritten;
                if (remainingColumns > 0)
                    Console.Write(new string(' ', remainingColumns));

                Console.WriteLine();
            }

            // Blank every row below the maze so stale content from a previously
            // taller overlay (hint screen, pause screen, etc.) cannot bleed through.
            Console.ResetColor();
            int firstBlankRow = Console.CursorTop;
            for (int blankRow = 0; blankRow < Console.WindowHeight - firstBlankRow - 1; blankRow++)
            {
                Console.SetCursorPosition(0, firstBlankRow + blankRow);
                Console.Write(fullBlankLine);
            }
        }

        /// <summary>
        /// Writes a full-screen text frame to the console without flickering.
        /// Each line is padded to the window width so stale characters from a
        /// previous frame are always overwritten rather than left visible.
        /// Lines beyond the supplied array are blanked out.
        /// </summary>
        /// <param name="lines">The lines of text to display, one per console row.</param>
        private static void DrawFrame(string[] lines)
        {
            Console.SetCursorPosition(0, 0);

            var frameBuffer = new StringBuilder();

            foreach (string line in lines)
            {
                frameBuffer.AppendLine(line.PadRight(Console.WindowWidth - 1));
            }

            // Blank every row below the supplied content so no stale text remains.
            string emptyRow = new string(' ', Console.WindowWidth - 1);
            for (int extraRow = lines.Length; extraRow < Console.WindowHeight - 1; extraRow++)
                frameBuffer.AppendLine(emptyRow);

            Console.Write(frameBuffer.ToString());
        }

        // =====================================================================
        // REGION: Enemy AI & Pathfinding
        // =====================================================================

        /// <summary>
        /// Moves the enemy one cell toward the player using the appropriate
        /// strategy for the current difficulty setting.
        /// </summary>
        /// <param name="maze">The maze grid, used for wall-collision checks.</param>
        private static void AdvanceEnemy(string[] maze)
        {
            if (isEnemySmart)
            {
                // Hard: use BFS to move directly toward the player each turn.
                var nextCell = FindNextStepTowards(maze, enemyX, enemyY, playerX, playerY);
                if (nextCell.HasValue)
                {
                    enemyX = nextCell.Value.X;
                    enemyY = nextCell.Value.Y;
                }
            }
            else
            {
                // Medium: random walk — pick a direction, move only if the target is open.
                int randomDirection = Rng.Next(4);
                int candidateX = enemyX + (randomDirection == 2 ? -1 : randomDirection == 3 ? 1 : 0);
                int candidateY = enemyY + (randomDirection == 0 ? -1 : randomDirection == 1 ? 1 : 0);

                bool withinBounds = candidateY >= 0 && candidateY < maze.Length &&
                                    candidateX >= 0 && candidateX < maze[candidateY].Length;

                if (withinBounds && maze[candidateY][candidateX] != TileWall)
                {
                    enemyX = candidateX;
                    enemyY = candidateY;
                }
            }
        }

        /// <summary>
        /// Uses breadth-first search to find the optimal first step from a source
        /// cell toward a goal cell through the open corridors of the maze.
        /// <para>
        /// The algorithm records each cell's parent during the forward BFS pass,
        /// then walks the parent chain backward from the goal to identify the
        /// immediate next step from the source — the only piece the caller needs.
        /// </para>
        /// </summary>
        /// <param name="maze">The maze grid defining which cells are traversable.</param>
        /// <param name="sourceX">Horizontal position of the starting cell.</param>
        /// <param name="sourceY">Vertical position of the starting cell.</param>
        /// <param name="goalX">Horizontal position of the target cell.</param>
        /// <param name="goalY">Vertical position of the target cell.</param>
        /// <returns>
        /// The coordinates of the cell immediately adjacent to the source that
        /// lies on the shortest path to the goal, or <see langword="null"/> if
        /// source equals goal or no path exists.
        /// </returns>
        private static (int X, int Y)? FindNextStepTowards(
            string[] maze, int sourceX, int sourceY, int goalX, int goalY)
        {
            if (sourceX == goalX && sourceY == goalY)
                return null;

            int gridHeight = maze.Length;
            int gridWidth = maze[0].Length;

            var visited = new bool[gridHeight, gridWidth];
            var parent = new (int X, int Y)?[gridHeight, gridWidth];
            var queue = new Queue<(int X, int Y)>();

            queue.Enqueue((sourceX, sourceY));
            visited[sourceY, sourceX] = true;

            // Cardinal direction offsets: North, South, West, East.
            int[] deltaX = { 0, 0, -1, 1 };
            int[] deltaY = { -1, 1, 0, 0 };

            bool goalFound = false;

            while (queue.Count > 0 && !goalFound)
            {
                var (currentX, currentY) = queue.Dequeue();

                for (int direction = 0; direction < 4; direction++)
                {
                    int neighbourX = currentX + deltaX[direction];
                    int neighbourY = currentY + deltaY[direction];

                    if (neighbourY < 0 || neighbourY >= gridHeight) continue;
                    if (neighbourX < 0 || neighbourX >= gridWidth) continue;
                    if (visited[neighbourY, neighbourX]) continue;
                    if (maze[neighbourY][neighbourX] == TileWall) continue;

                    visited[neighbourY, neighbourX] = true;
                    parent[neighbourY, neighbourX] = (currentX, currentY);

                    if (neighbourX == goalX && neighbourY == goalY)
                    {
                        goalFound = true;
                        break;
                    }

                    queue.Enqueue((neighbourX, neighbourY));
                }
            }

            if (!goalFound)
                return null;

            // Walk the parent chain from the goal back to the cell directly after
            // the source to find the immediate first step.
            int traceX = goalX;
            int traceY = goalY;

            while (parent[traceY, traceX].HasValue)
            {
                var parentCell = parent[traceY, traceX].Value;

                if (parentCell.X == sourceX && parentCell.Y == sourceY)
                    return (traceX, traceY);

                traceX = parentCell.X;
                traceY = parentCell.Y;
            }

            return null;
        }

        /// <summary>
        /// Locates the exit tile in the maze and uses <see cref="FindNextStepTowards"/>
        /// to determine the optimal first move, then displays the cardinal direction
        /// to the player as a single-line hint.
        /// </summary>
        /// <param name="maze">The maze grid being played.</param>
        private static void ShowHint(string[] maze)
        {
            // Scan the grid to find the exit cell coordinates.
            int exitX = -1, exitY = -1;

            for (int row = 0; row < maze.Length && exitY < 0; row++)
                for (int col = 0; col < maze[row].Length && exitY < 0; col++)
                    if (maze[row][col] == TileExit) { exitX = col; exitY = row; }

            var nextStep = FindNextStepTowards(maze, playerX, playerY, exitX, exitY);

            string directionAdvice;

            if (nextStep.HasValue)
            {
                int deltaX = nextStep.Value.X - playerX;
                int deltaY = nextStep.Value.Y - playerY;

                // Map the one-cell delta onto a compass direction with the matching key binding.
                directionAdvice = deltaY < 0 ? "Head NORTH (W / Up)" :
                                  deltaY > 0 ? "Head SOUTH (S / Down)" :
                                  deltaX < 0 ? "Head WEST  (A / Left)" :
                                               "Head EAST  (D / Right)";
            }
            else
            {
                directionAdvice = "You're already at the exit!";
            }

            string[] frame =
            {
                "",
                "  ╔══════════════════════════════════╗",
                "  ║              HINT                ║",
                "  ╚══════════════════════════════════╝",
                "",
                "  " + directionAdvice,
                "",
                "  Press any key...",
                ""
            };

            DrawFrame(frame);
            PlayBeep(1000, 100);
            Console.ReadKey(true);
        }

        // =====================================================================
        // REGION: Persistence
        // High scores are stored as a plain-text file of padded pipe-delimited
        // entries so that sorting by step count can be done with a simple string
        // comparison on the left-padded numeric prefix.
        // =====================================================================

        /// <summary>
        /// Appends the current session result to the high-score file and retains
        /// only the top <see cref="MaxHighScores"/> entries, sorted by step count
        /// ascending (fewer steps = better performance).
        /// </summary>
        /// <param name="stepCount">Total steps taken across all mazes.</param>
        /// <param name="elapsed">Wall-clock time for the completed session.</param>
        private static void SaveScore(int stepCount, TimeSpan elapsed)
        {
            var existingScores = new List<string>();

            if (File.Exists(HighScoreFilePath))
                existingScores = File.ReadAllLines(HighScoreFilePath).ToList();

            // The left-padded numeric prefix makes the pipe-delimited lines
            // sortable as plain strings without needing to parse the number first.
            string displayText = stepCount + " steps in " + FormatElapsedTime(elapsed)
                + "  [" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "]";

            string storageLine = stepCount.ToString().PadLeft(8) + "|" + displayText;
            existingScores.Add(storageLine);

            var topScores = existingScores
                .OrderBy(line => int.Parse(line.Split('|')[0].Trim()))
                .Take(MaxHighScores)
                .ToList();

            File.WriteAllLines(HighScoreFilePath, topScores);
        }

        // =====================================================================
        // REGION: Audio & Utility Helpers
        // Small, single-purpose methods that are called from multiple places.
        // =====================================================================

        /// <summary>
        /// Plays a beep through the system speaker if audio is currently enabled.
        /// Silently swallows any exception so that environments without speaker
        /// support (e.g. some CI runners or headless terminals) do not crash.
        /// </summary>
        /// <param name="frequencyHz">Tone frequency in hertz (37–32767).</param>
        /// <param name="durationMs">Duration of the tone in milliseconds.</param>
        private static void PlayBeep(int frequencyHz, int durationMs)
        {
            if (soundEnabled)
                try { Console.Beep(frequencyHz, durationMs); } catch { }
        }

        /// <summary>
        /// Formats a <see cref="TimeSpan"/> as <c>MM:SS</c> for display in the
        /// HUD and score screens.
        /// </summary>
        /// <param name="elapsed">The duration to format.</param>
        /// <returns>A string in the form <c>"02:47"</c>.</returns>
        private static string FormatElapsedTime(TimeSpan elapsed)
            => $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }
}