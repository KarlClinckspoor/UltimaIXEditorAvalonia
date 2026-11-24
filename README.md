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

Placing objects: the Place object tab lists every type (those already on the map first, with how many); pick one,
press Place and click the ground. The new record copies flags and model from one of its kind on the map, or failing that on any of the
game's maps (read once, the first time it is needed); which file it goes in follows the same counts.

Editing map links (Map links tab): select a link (or click its yellow zone), then Retarget selected sends it to the
map and link in the fields; the zone gets a new TRIGGERS.FLX script slot, since 68 shipped scripts serve several
zones. Free link finds an unused link number in the destination map; New link at point copies a zone to the
clicked ground point; New arrival at point puts a violet arrival marker (type 1134) with the link there. Saving
writes TRIGGERS.FLX too (original kept as .orig).

Editing objects: click an object (its model, or its marker with models off) to select it; drag it to move it (it
keeps its height above the ground unless Edit > Dragged objects follow the terrain is off; Esc puts it back).
The Selected object panel takes exact X, Row (points), Z (object units) and a turn in degrees. Keys: `[` / `]`
turn 15° (Shift 1°), Page Up / Page Down raise and lower, End puts it on the ground, Ctrl+D copies, Delete
deletes, Ctrl+Z / Ctrl+Y undo and redo, Ctrl+S saves. Saving writes `static/fixed.N` and `runtime/nonfixed.N` of
every changed map; the first save keeps each original beside it as `.orig`. Saved games most likely carry their
own copy of the nonfixed objects (not checked yet), so try nonfixed changes in a new game.

Editing terrain (Terrain tab, or B): with the brush on, a left drag on the ground raises, lowers, smooths,
flattens (to a typed height, or the height under the first dab), paints a texture and frame, or cuts and fills
holes; right drag still orbits. Holding the mouse still keeps the height tools working. Objects standing on the
ground (within 16 object units) go up and down with it; things on tables and floors stay where they are. Each
stroke is one undo step, objects included. Painting a texture the map did not use adds it to the view's textures.
With Blend edges on, painting lays the texture as ground on the points' corners and gives the quads along the
edge the map's transition frames into what was there, as the shipped maps do. Which plain textures a transition
set blends is learnt from the map itself: plain quads beside transition quads vote for the side they touch (on
Britannia set 10 is grass 8 into dirt 11, 11,896 and 14,602 votes against a few hundred); 13 of Britannia's 14
sets are clear, and the panel says which textures the chosen one blends into. The map's own transition quads come
back from their corner materials in over 99% of cases. Against ground with no transition the edge stays hard.
The Terrain tab shows the map's ground textures as thumbnails, most used first (the tooltip says what each blends
into); clicking one paints it. With the brush on, `[` / `]` change its radius and Ctrl+click takes the height (Flatten) or the texture (Paint;
with blending, the material on the clicked side of a transition quad) from the ground. Saving
writes `static/terrain.N` (first time keeping `.orig`). Checked in the game: a hill raised at Britannia's bridge
(height 607 to 907) put the Avatar on its top at z 3626 (the editor reckons 3628).

Start options for scripted pictures: `--look X,ROW --distance D --pitch P --yaw Y`.
`u9 export-web <gamedata> <out> [--maps 9,10] [--texture-max 128] [--no-models]` bakes maps for the WebGL viewer in
UltimaCartographer (`u9viewer/EXPORT.md` is the contract); it prints a size report (Britannia alone: 73 MB, of which
37 MB are 128-pixel model textures; every map: 180 MB, 14 s).
Agents drive the running editor with `.claude/skills/run-u9editor/driver.ps1` (see its `SKILL.md`).

## Format status

| Format | Read | Write | Byte-identical round trip |
|---|---|---|---|
| FLX archives (`*.flx`, `texture8.N`, `texture16.N`) | yes | yes (preserve or compact layout) | all 29 shipped archives |
| Terrain (`terrain.N`) | yes | yes | all 168 shipped maps |
| Bitmaps (`bitmap16.flx`: terrain and model textures) | yes | not yet | — |
| Fixed / nonfixed objects (`fixed.N`, `nonfixed.N`) | yes | move, turn, copy, delete (with undo) | all shipped files |
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
- Object files: a chunk (32 x 32 points) is a chain of 4 KB pages. Only its first page's 16 list heads are used
  (later pages hold leftovers there); each list is one subchunk, `x / 1024 + 4 * (y / 1024)` inside the chunk, and
  runs through records in any of the chunk's pages. A page's +0x04 is the offset of its first free byte, +0x14 the
  records in it and (nonfixed) +0x18 its property segments. The u32 before the data is a free page list: the
  first unused page | 1, whose +0x00 names the next. Fixed pages are packed; nonfixed pages keep holes.
  Moving an object to another chunk moves its record (and property segment) into that chunk's pages.
- Terrain point bits 13-14 turn the quad's texture in quarter turns (not "swap" and "mirror"); turn 0 puts the
  texture's top row to the north. Found from the 9-frame transition textures (frames 0-2 outer corner, 3-5 inner
  corner, 6-8 edge): neighbouring transition quads agree on their shared corners in 98.2% of Britannia's 80,498
  pairs under this reading, 40% under swap/mirror. Bit 15 does not orient the texture. All three bits are spread
  evenly over plain ground too, which is turned at random to hide the tiling. `u9 bitmap-frames` shows a set.
- Bitmaps: pure magenta (RGB565 0xF81F, ARGB1555 0x7C1F, even with the opaque bit) is a colour key for
  transparent pixels. Model faces with flag bit 3 are not drawn: they are the untextured magenta blocking walls
  (579 of the 580 magenta untextured faces in `sappear.flx`, and no other face), such as Moonglow's.
- Bitmap pixel format is the low nibble of frame byte 1: 3 = 8-bit intensity, 4 = RGB565, 5 = ARGB1555.
