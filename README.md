# U9 Editor

A map editor for Ultima IX: Ascension, in C# with Avalonia (modelled on UltimaUnderworldEditorAvalonia).

## Layout

| Project | What it is |
|---|---|
| `src/U9.Formats` | Readers and writers for the game's files. No UI. |
| `src/U9Editor.Core` | Map space, camera, terrain raster and picking. No UI, no GL. |
| `src/U9Editor.Render` | OpenGL 3.3 / GLES 3.0 terrain renderer (Silk.NET), toolkit-agnostic. |
| `src/U9Editor.App` | The Avalonia editor: 3D view, map list, view options. |
| `src/U9Editor.Cli` | `u9` command line: dump, unpack, debug pictures. |
| `tests/U9.Formats.Tests` | Round-trip tests over every shipped file. |
| `tests/U9Editor.Core.Tests` | Picking and map-space tests. |
| `analysis/` | Ghidra notes on `u9.exe`. |
| `gamedata/` | Your copy of the GOG install (not committed). Or set `U9_GAMEDATA`. |


## Build and test

```
dotnet build U9Editor.sln
dotnet test U9Editor.sln
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll terrain-info gamedata/static/terrain.9
dotnet run --project src/U9Editor.App -- --gamedata gamedata --map 9
```

Editor options: `--gamedata PATH`, `--map N`, `--size WxH`, `--top-down`, `--screenshot out.png` (saves the first
frame and quits). In the view: left drag orbits, middle or Shift+drag pans, wheel zooms, W/A/S/D move, Q/E turn,
R/F raise and lower, Tab toggles the top-down map, Home frames the map, click selects a point or object (shown in
the status bar). The Links panel lists the current map's map changes; double-click goes to where one leads.
Start options for scripted pictures: `--look X,ROW --distance D --pitch P --yaw Y`.

## Format status

| Format | Read | Write | Byte-identical round trip |
|---|---|---|---|
| FLX archives (`*.flx`, `texture8.N`, `texture16.N`) | yes | yes (preserve or compact layout) | all 29 shipped archives |
| Terrain (`terrain.N`) | yes | yes | all 168 shipped maps |
| Bitmaps (`bitmap16.flx`: terrain and model textures) | yes | not yet | — |
| Fixed / nonfixed objects (`fixed.N`, `nonfixed.N`) | yes | in place (move within a chunk) | all shipped files |
| Object types (`TYPES.DAT`, `TYPENAME.FLX`) | yes | not yet | — |
| Models (`sappear.flx`) | yes (all 3,748 models, every LOD) | not yet | — |
| Trigger scripts (`TRIGGERS.FLX`): map changes | yes | not yet | — |

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

Map space (used by the editor):

- North is the last point row; row 0 is south and x runs east. Checked against the game's own map of Britannia
  (`bitmap16.flx` record 534): Castle British (Lord British's throne in `nonfixed.9` at point 502, 554) is in the
  north, Terfin and Ambrosia in the south.
- Fixed and nonfixed objects are placed at 128 units per point, and their height is 4 x the terrain height
  (median 3.99 over 21,853 objects in `fixed.9`), so one point spacing is 32 height units. The water level is in
  the same object units.
- Objects: model space is x east, y north, z up in object units; an object's quaternion (int16 x, y, z, w at
  +0x0C) is applied as stored (not conjugated), then its position; fronts face south. Checked on fence chains
  and Castle British.
- Map changes: a nonfixed trigger zone (usually type 1111) names a `TRIGGERS.FLX` script whose op 0x1F sends the
  player to the object with a matching link in the destination map. The cave between Buccaneer's Den and New
  Magincia is map 89 ("Buc2NewMag"), entered at points (573.0, 262.4) and (629.0, 248.9) on Britannia.
- Bitmap pixel format is the low nibble of frame byte 1: 3 = 8-bit intensity, 4 = RGB565, 5 = ARGB1555.
