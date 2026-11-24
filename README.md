# U9 Editor

A map editor for **Ultima IX: Ascension** (Origin, 1999), written in C# (.NET 10) with Avalonia and OpenGL.
It reads and writes the game's own files, so changes show up in the real game.

> Fan project, not affiliated with or endorsed by Electronic Arts or Origin Systems. Ultima is a trademark of
> Electronic Arts. No game files are included: you need your own copy of the game (the GOG release works).

## What it does

- **3D view of every map**: terrain with its textures and transitions, water, objects with their models,
  trigger zones and map links. Orbit, pan, fly with W/A/S/D, or a top-down map view.
- **Objects**: select, move (keeping height above the ground), turn, raise, copy, delete and place new ones of
  any of the game's types, with undo and redo. Saves `fixed.N` and `nonfixed.N`.
- **Map links**: retarget a door or cave to another map and arrival point, add new links and arrivals. Saves
  the trigger scripts in `TRIGGERS.FLX`.
- **Terrain**: brushes to raise, lower, smooth, flatten, roughen, paint textures (with the map's own blended
  edges) and cut holes; objects on the ground follow it. Change the sea level. Saves `terrain.N`.
- **Formats**: readers and writers for FLX archives, terrain, object files, types, models and bitmaps, tested
  for byte-identical round trips over every shipped file.
- Saving keeps each original file beside it as `.orig` the first time.

Checked in the game: a retargeted cave link takes the Avatar to the new arrival, a raised hill puts him on
its top at the height the editor reckons, objects that followed the ground sit on it, and painted textures blend
as in the editor.

## Status and roadmap

Usable for object, link, terrain, contents and model editing on any map. Work is tracked as
[milestones](https://github.com/KarlClinckspoor/UltimaIXEditorAvalonia/milestones):

1. **In-game verification** (done): confirm terrain painting, blended edges and objects following the ground in the game.
2. **Multi-select and group editing** (done): box and Ctrl+click selection; move, turn, copy and delete groups as one undo step.
3. **Prefabs**: copy and paste a group of objects with its ground, also between maps.
4. **Search and navigation** (done): find objects by type or name, jump to link destinations, bookmarks.
5. **Object contents** (done): book texts, container contents, NPC and object properties.
6. **New models through Blender** (done): import and export `sappear.flx` models via glTF.
7. **Cutscene viewer and editor**: play the game's in-engine cutscenes in the 3D view (starting with the first one,
   the camera entering the Avatar's house) and edit their camera moves, timing and actors.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a copy of Ultima IX. Copy (or link) the
installed game folder to `gamedata/` in the repository, or point `U9_GAMEDATA` at it. Work on a copy: the editor
writes into the game's files.

```
dotnet build U9Editor.sln
dotnet run --project src/U9Editor.App -- --gamedata gamedata --map 9
```

Map 9 is Britannia. Developed and tested on Windows; the editor needs OpenGL 3.3 (or GLES 3.0).

## Layout

| Project | What it is |
|---|---|
| `src/U9.Formats` | Readers and writers for the game's files. No UI. |
| `src/U9Editor.Core` | Map space, camera, terrain raster, picking, editing and search. No UI, no GL. |
| `src/U9Editor.Render` | OpenGL 3.3 / GLES 3.0 terrain renderer (Silk.NET), toolkit-agnostic. |
| `src/U9Editor.App` | The Avalonia editor: 3D view, map list, view options. |
| `src/U9Editor.Cli` | `u9` command line: dump, unpack, debug pictures. |
| `tests/U9.Formats.Tests` | Round-trip tests over every shipped file. |
| `tests/U9Editor.Core.Tests` | Picking and map-space tests. |
| `analysis/` | Survey notes on `u9.exe`. |
| `tools/` | Helper scripts (in-game testing). |
| `gamedata/` | Your copy of the GOG install (not committed). Or set `U9_GAMEDATA`. |


## Build, test and use

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

Several objects: Ctrl+click adds an object to the selection or takes it away, and Ctrl+drag selects every object
drawn inside the box (added to what is selected). Dragging any of them moves them all (each keeping its height above
the ground); `[` / `]` turn the group about its middle, positions and facings together; Page Up / Page Down, End,
Ctrl+D (copies one point east, then selected) and Delete act on all of them, each as one undo step over both
object files. The panel's fields still show and set the last object picked. Esc clears the selection.

What objects carry (Contents tab): a nonfixed object's properties by the game's own names (Book Number, Quantity,
Light Range, Creature Type, Spawn Percent... 69 of them), chained segments included; pick one and Set writes its two
values (one undo step). Inside lists what a container holds or an NPC carries (with quantities); picking an item
shows its properties, to change say the coins in a bag. For anything with a Book Number (books, plaques, signs,
grave markers) the panel shows the book's title and text: Set text changes them (one undo step), and Ctrl+S writes
`static/BOOKS-EN.FLX` (first time keeping `.orig`). The book's number can be changed like any property, to point
an object at another text.

Models through Blender: File > Export selected object's model (glTF) writes a `.glb` with its textures embedded
(x east, y north, z up as Blender shows it; one terrain point = 1 m). Edit it in Blender and export it again as glTF
with Include > Custom Properties on, so each material keeps the game's texture, frame, face flags and colour. File >
Replace selected object's model from glTF writes the triangles into `static/sappear.flx` (first time keeping
`.orig`); every object showing that model, on every map, changes. The command line does the same:
`u9 model-export <gamedata> <record> <out.glb>`, `u9 model-import <gamedata> <record> <in.glb>`, and `u9 model-info
<file.glb>` shows what the editor reads from a file. Checked in the game: Britain's barrel (model 23), exported,
made three times as wide in Blender and imported, is drawn by the game with its texture at the archery yard.
Materials without the u9 properties become untextured faces in their base colour; new textures are not imported
yet, and an imported model has one limb and one level of detail.

Finding things (Find tab): type words of a type's name (`telescope`, `orb moons`), a type number (`2894`), `#id`
of an object here, `link:n` of a nonfixed object, or `fixed` / `nonfixed`, and press Enter; all words must match.
All maps searches every map's object files (open maps with their unsaved edits); the first time reads them all.
Double-clicking a result goes there (opening its map) and selects it on this map; Select all found here makes the
results a selection, to move or delete together. Go to takes `x, row` or `map, x, row`. Bookmarks remember a map,
the point looked at and the camera; a few start the list (Castle British, Moonglow, the bridge west of Britain,
the cave to New Magincia), and they are kept in `%AppData%/U9Editor/settings.json`.

Editing terrain (Terrain tab, or B): with the brush on, a left drag on the ground raises, lowers, smooths,
flattens (to a typed height, or the height under the first dab), roughens (smooth noise bumps), paints a texture and frame, or cuts and fills
holes; right drag still orbits. Holding the mouse still keeps the height tools working. Objects on the ground go
up and down with it: up to 16 object units above it or 64 sunk into it (many are sunk on purpose: on map 4
plants and the pools' stones sit 45 to 61 below); things on tables and floors stay where they are. Each
stroke is one undo step, objects included. Painting a texture the map did not use adds it to the view's textures.
With Blend edges on, painting lays the texture as ground on the points' corners and gives the quads along the
edge the map's transition frames into what was there, as the shipped maps do. Which plain textures a transition
set blends is learnt from the map itself: plain quads beside transition quads vote for the side they touch (on
Britannia set 10 is grass 8 into dirt 11, 11,896 and 14,602 votes against a few hundred); 13 of Britannia's 14
sets are clear, and the panel says which textures the chosen one blends into. The map's own transition quads come
back from their corner materials in over 99% of cases. Against ground with no transition the edge stays hard.
Water height in the Terrain tab moves the map's sea level (in point heights, as the status bar shows them; the
terrain header stores four times that), as one undo step saved with the terrain.
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

## Technical notes

### Format status

| Format | Read | Write | Byte-identical round trip |
|---|---|---|---|
| FLX archives (`*.flx`, `texture8.N`, `texture16.N`) | yes | yes (preserve or compact layout) | all 29 shipped archives |
| Terrain (`terrain.N`) | yes | yes: heights, textures, holes, water level (brushes with undo) | all 168 shipped maps |
| Bitmaps (`bitmap16.flx`: terrain and model textures) | yes | not yet | — |
| Fixed / nonfixed objects (`fixed.N`, `nonfixed.N`) | yes | move, turn, copy, delete, place (with undo) | all shipped files |
| Object types (`TYPES.DAT`, `TYPENAME.FLX`) | yes | not yet | — |
| Models (`sappear.flx`) | yes (all 3,748 models, every LOD) | one limb, one LOD, from glTF (checked in the game) | — |
| Trigger scripts (`TRIGGERS.FLX`): map changes | yes | map-change scripts (retarget, new links) | unchanged slots kept |
| Book texts (`BOOKS-EN.FLX`) | yes | yes (titles and texts) | all 460 records |
| Object properties (nonfixed property segments) | yes, named, chains followed, contents | values | — |

### Findings from the shipped files

- FLX: some archives have records out of order, with stale bytes between them (the original tools appended
  grown records and left the old space). The preserve layout keeps both, so edits change as few bytes as possible.
- Terrain: the header's width and height are in points, not tiles (Britannia is 1024 x 1024 points, 64 x 64 tiles).
- Terrain: the water level is in quarter height units. Britannia's 2024 is point height 506, where its coast is.
  The water level is signed (-12 on the Abyss). The wave amplitude is a float.
- Terrain: 22 files hold stale chunk data after the last chunk, and 3 are cut off after the header (width 0, but
  chunks claimed). The name field has leftover bytes after its NUL. All of this is kept on write.
- Terrain: points inside a chunk are stored row by row (x + y * 16), and tiles row by row. The Britannia
  heightmap from `u9 terrain-heightmap` shows no seams.

### Map space (used by the editor)

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
- Object properties: u9.exe names them in a table of 69 name pointers (file offset 0x38D310 in the GOG exe)
  followed by a parallel table of their ids (0x38D440): 0x05 Contents, 0x11 Quantity, 0x4D Book Number,
  0x21-0x23 Light Color / Range / Flicker, 0x80-0x83 Creature Type, Spawn Percent, Max Of Type, Creature Link Num,
  0x90-0x94 the ambiance sounds, 0xFF Next Property Pointer and so on. Checked on the shipped objects: Book Number
  sits on books, plaques and grave markers, Quantity on coins and arrows, the creature ones on monster generators.
  For Contents and Next Property Pointer the two u16 values are one 32-bit data offset (A low): Next Property
  Pointer leads to the object's next 16-byte segment (monster generators keep their spawn settings there), Contents
  to the first record inside it, and those records chain through their link field (+0x00). Contained records are
  in no subchunk list (a bag on Britannia holds coins, two silks and a white potion).
- Models (`sappear.flx`): the header's +0x08 is the centre of the vertices' box, +0x14 its height, +0x18 the
  distance from that centre to the farthest vertex, +0x1C / +0x28 the same centre and radius again, +0x30 / +0x3C the
  box. The box's maximum starts at FLT_MIN rather than -FLT_MAX in the original tool, so no maximum is below ~0
  (that reproduces 3,008 of 3,112 one-limb headers; the rest keep vertices no face uses). +0x58 repeats the centre;
  +0x64 is not understood. LOD blocks repeat centre, radius and box at +0x0C / +0x18 / +0x1C. A face's plane is at
  +0x5C (normal) and +0x68 (distance, n·p + d = 0). The four lists after the materials are draw orders, 0xFFFF and
  every face, padded to 4 bytes, running roughly along +x, +y, -x and -y. Materials: +0 texture, +8 first face,
  +0xA count, +0xC 0xFFFF, +0xE / +0xF first and last frame.
- Books: `BOOKS-EN.FLX` record n - 1 is book number n (titles start "n: "): a u32 length and the NUL-terminated
  title, a u32 length and the text, CR LF lines and `` `f1 `` / `` `f2 `` font codes. Record 160 holds an embedded
  document after its title (a leftover); record 4 is a commented "testshop" template.
- Object pages: +0x04 is not a dependable end of a page's data. In the shipped fixed files it points inside or
  before the live records in 1,390 of 3,175 pages (in fixed.4's chunk 7,7 it is 0xC0 with records from 0xF0), and
  nonfixed pages hold records no list reaches (contained items). New records go above +0x04, every live record and
  the page's last non-zero byte; a test crowds the busiest chunks of all 328 shipped object files and checks that no
  other object changes (trusting +0x04, 169 files failed: copies overwrote objects).
- Object files: a chunk (32 x 32 points) is a chain of 4 KB pages. Only its first page's 16 list heads are used
  (later pages hold leftovers there); each list is one subchunk, `x / 1024 + 4 * (y / 1024)` inside the chunk, and
  runs through records in any of the chunk's pages. A page's +0x04 is meant as the offset of its first free byte (not dependable, see above), +0x14 the
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
  (579 of the 580 magenta untextured faces in `sappear.flx`, and no other face), such as on map 4 (an unused development map named Moonglow; the city is on Britannia).
- Bitmap pixel format is the low nibble of frame byte 1: 3 = 8-bit intensity, 4 = RGB565, 5 = ARGB1555.
- Terrain transition sets: each 9-frame texture blends two plain textures, and which two can be read off the map:
  plain quads beside a transition quad vote for the material on the side they touch. On Britannia 13 of its 14
  sets come out clear (set 10 is grass 8 into dirt 11 by 11,896 and 14,602 votes against a few hundred; set 23 is
  ambiguous), and the map's 30,000-odd transition quads come back from their corner materials, frame group and
  turn included, in over 99% of cases. That is what the editor's blended painting uses.
- Terrain chunks: Britannia shares none (5,588 chunks for 4,096 tiles, the rest stale); map 4 shares widely
  (504 chunks for 1,024 tiles). An edit gives a tile its own copy first; saving shares copies that match again.
- Objects and the ground: many fixed objects are sunk into the terrain on purpose. On map 4 only 315 of 1,218
  are within 16 object units of the ground and 698 within 64; plants and the pools' stones sit 45-61 below.
- In the game (patched U9GL, `tools/game-test.ps1`): a link retargeted and saved by the editor takes the Avatar
  to the new arrival (59779,64640 for 59776,64640), and a hill raised and saved puts him on its top at the height
  the editor reckons (z 3626 for 3628). At Moonglow (Britannia, around point 744, 396) a hill raised with
  objects following, and sand painted with blended edges, look in the game as in the editor: the mushrooms,
  rocks and bushes that followed sit on the new slope, and the sand blends into grass (into rock, which has no
  transition set with sand, the edge stays hard, as the editor shows it).

## License

MIT (see [LICENSE](LICENSE)). This covers the editor's code only, not the game or its data.
