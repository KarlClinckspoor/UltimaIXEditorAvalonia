# U9 Editor

A map editor for Ultima IX: Ascension, in C# with Avalonia (modelled on UltimaUnderworldEditorAvalonia).

## Layout

| Project | What it is |
|---|---|
| `src/U9.Formats` | Readers and writers for the game's files. No UI. |
| `src/U9Editor.Cli` | `u9` command line: dump, unpack, debug pictures. |
| `tests/U9.Formats.Tests` | Round-trip tests over every shipped file. |
| `analysis/` | Ghidra notes on `u9.exe`. |
| `gamedata/` | Your copy of the GOG install (not committed). Or set `U9_GAMEDATA`. |

Still to come: `U9Editor.Core`, `U9Editor.Render`, `U9Editor.App`.

## Build and test

```
dotnet build U9Editor.sln
dotnet test U9Editor.sln
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll terrain-info gamedata/static/terrain.9
```

## Format status

| Format | Read | Write | Byte-identical round trip |
|---|---|---|---|
| FLX archives (`*.flx`, `texture8.N`, `texture16.N`) | yes | yes (preserve or compact layout) | all 29 shipped archives |
| Terrain (`terrain.N`) | yes | yes | all 168 shipped maps |

Findings from the shipped files, beyond the public notes:

- FLX: some archives have records out of order, with stale bytes between them (the original tools appended
  grown records and left the old space). The preserve layout keeps both, so edits change as few bytes as possible.
- Terrain: the header's width and height are in points, not tiles (Britannia is 1024 x 1024 points, 64 x 64 tiles).
- Terrain: the water level is in quarter height units. Britannia's 2024 is point height 506, where its coast is.
  The water level is signed (-12 on the Abyss). The wave amplitude is a float.
- Terrain: 22 files hold stale chunk data after the last chunk, and 3 are cut off after the header (width 0, but
  chunks claimed). The name field has leftover bytes after its NUL. All of this is kept on write.
- Terrain: points inside a chunk are stored row by row (x + y * 16), and tiles row by row. The Britannia
  heightmap from `u9 terrain-heightmap` shows no seams.
