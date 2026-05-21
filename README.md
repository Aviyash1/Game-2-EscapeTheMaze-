# Escape The Maze

## Overview
Escape The Maze is a console-based adventure game built in C# using Visual Studio. The objective is to navigate through multiple maze levels, avoid obstacles, and reach the exit while completing each maze in the shortest number of steps possible.

The game progressively increases in difficulty and includes enemy AI, fog of war, hint systems, and score tracking to enhance gameplay depth and challenge.

---

## Features

### Core Gameplay
- Player movement using **WASD** or arrow keys
- Collision detection with walls (`#`)
- Exit detection (`E`) to complete each maze
- Multiple maze levels (3 stages)

### Game Systems
- Step-based scoring system (lower steps = better score)
- Pause and resume functionality (P key)
- Sound effects using `Console.Beep()`
- Game over system (caught by enemy)

### Enemy AI
- Random enemy movement (Easy/Medium)
- Smart BFS-based chasing AI (Hard mode)
- Enemy collision ends the game

### Difficulty Levels
- **Easy**: No enemy, full visibility
- **Medium**: Random moving enemy + fog of war
- **Hard**: Smart chasing enemy + fog of war

### Advanced Mechanics
- BFS-based hint system (H key)
- Fog of War (limited vision range)
- Trail system showing player movement history
- Countdown timer before game starts
- Multi-maze progression system
- High score system (top 10 stored locally)

---

## Controls

| Key | Action |
|-----|--------|
| W / ↑ | Move Up |
| S / ↓ | Move Down |
| A / ← | Move Left |
| D / → | Move Right |
| P | Pause / Resume |
| H | Show Hint |
| ESC | Quit Game |

---

## Data Structures Used

This project uses multiple data structures to manage game logic efficiently:

- `string[]` → Maze layout representation
- `List<(int x, int y)>` → Player trail tracking
- `Queue<(int x, int y)>` → BFS pathfinding algorithm
- `bool[,]` → Visited nodes in BFS
- `parent[,]` array → Path reconstruction for hint system
- `List<string>` → High score storage and sorting

These structures allow efficient movement, pathfinding, and persistent score tracking.

---

## Algorithms Used

### Breadth-First Search (BFS)
Used in:
- Hint system (H key)
- Smart enemy AI (Hard mode)

BFS ensures the shortest path is found between two points in the maze.

### Sorting Algorithm
Used in:
- High score system (Top 10 steps)

Scores are sorted so that the best (lowest step count) runs appear first.

---

## High Score System
- Stores top 10 best runs
- Saved locally in `highscores.txt`
- Sorted by lowest step count (best performance)

---

## Sound System
- Movement sound effects
- Invalid move feedback
- Level completion sounds
- Game over sound feedback

Uses `System.Console.Beep()`.

---

## How to Run

1. Open project in Visual Studio
2. Build solution (`Ctrl + Shift + B`)
3. Run program (`F5`)
4. Follow main menu instructions

---

## File Structure
