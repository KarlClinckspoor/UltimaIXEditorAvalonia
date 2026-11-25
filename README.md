# U9 Editor

A map editor for **Ultima IX: Ascension** (Origin, 1999), written in C# (.NET 10) with Avalonia and OpenGL.
It reads and writes the game's own files, so changes show up in the real game.

> Fan project, not affiliated with or endorsed by Electronic Arts or Origin Systems. Ultima is a trademark of
> Electronic Arts. No game files are included: you need your own copy of the game (the GOG release works).

## What it does

- **3D view of every map**: terrain with its textures and transitions, water, objects with their models,
  trigger zones and map links. An orbit camera, a fly camera (V), or a top-down map view. A tool bar beside the
  view picks what a drag does (Select, Move, Rotate, Place, Terrain), so looking round never moves anything.
- **Objects**: select, move (keeping height above the ground), turn, raise, copy, delete and place new ones of
  any of the game's types, with undo and redo. Saves `fixed.N` and `nonfixed.N`.
- **Map links**: retarget a door or cave to another map and arrival point, add new links and arrivals. Saves
  the trigger scripts in `TRIGGERS.FLX`.
- **Terrain**: brushes to raise, lower, smooth, flatten, roughen, paint textures (with the map's own blended
  edges) and cut holes; objects on the ground follow it. Change the sea level. Saves `terrain.N`.
- **Scenes**: the game's in-engine scenes (camera moves chained by trigger scripts), listed per map and played
  in the 3D view; change a move's duration and style, put a camera point where the view is, edit any script as
  text, and make new scenes: a trigger zone and camera points from the view, move by move, or a camera circling an
  object. Saves `TRIGGERS.FLX` and `nonfixed.N`.
- **Prefabs**: copy a group of objects with the ground under it and paste it elsewhere, on the same map or another,
  turned in quarter turns, its edge blended into the ground there. Objects come whole (properties, chained
  properties, what containers hold), with new link numbers and copies of the scripts they run. Prefabs can be saved
  to a library and pasted into any map later.
- **Combat balance** (Edit > Combat balance, Ctrl+B): how many hits the Avatar needs to kill each creature with each
  weapon, and how many each creature needs to kill the Avatar in each armour set, worked out from the game's own
  tables and damage rules; and editing of weapons, shields, armour, creatures' natural attacks and damage multipliers
  (tables in `u9.exe`, kept as patches) and creatures' HP, defence and kind (`runtime/NPC.FLX`).
- **Dialogue** (Edit > Dialogue, Ctrl+G): every conversation line (`static/text.flx`) by conversation, with its voice
  clip (`sound/Speech.flx`, EA MicroTalk, decoded to play or save as WAV); edit lines with a live check of what the
  game will show (its code page, the subtitle font's glyphs, wrapping in the 600 px box, pages, timing) and replace
  a voice from a WAV.
- **Mods**: File > Export changes as mod makes a `.u9mod` of what the editor saved (differences only, never the game's
  files). The **U9 Mod Manager** (`src/U9ModManager`, and `u9mod` on the command line) applies several mods at once,
  merging their changes and refusing clashes, puts the original game back, downloads mods from a catalog and fetches
  U9GL. `u9.exe` is never changed: executable patches go into `u9-mod.exe`. See [docs/mod-format.md](docs/mod-format.md);
  what data can change and what needs the executable: [docs/modding.md](docs/modding.md).
- **Look**: a light, compact, classic theme by default (framed lists and tabs, gradient buttons) and a dark one
  (View > Theme).
- **Formats**: readers and writers for FLX archives, terrain, object files, types, models and bitmaps, tested
  for byte-identical round trips over every shipped file.
- Saving keeps each original file beside it as `.orig` the first time.

Checked in the game: a retargeted cave link takes the Avatar to the new arrival, a raised hill puts him on
its top at the height the editor reckons, objects that followed the ground sit on it, and painted textures blend
as in the editor. The opening scene on Earth plays in the game as the editor shows it, and a camera point moved in
the editor changes it there.

## Status and roadmap

Usable for object, link, terrain, contents and model editing on any map. Work is tracked as
[milestones](https://github.com/KarlClinckspoor/UltimaIXEditorAvalonia/milestones):

1. **In-game verification** (done): confirm terrain painting, blended edges and objects following the ground in the game.
2. **Multi-select and group editing** (done): box and Ctrl+click selection; move, turn, copy and delete groups as one undo step.
3. **Prefabs** (done): copy and paste a group of objects with its ground, also between maps; a library of saved prefabs.
4. **Search and navigation** (done): find objects by type or name, jump to link destinations, bookmarks.
5. **Object contents** (done): book texts, container contents, NPC and object properties.
6. **New models through Blender** (done): import and export `sappear.flx` models via glTF.
7. **Cutscene viewer and editor** (camera part done): play the game's in-engine cutscenes in the 3D view (starting
   with the first one, the camera entering the Avatar's house) and edit their camera moves and timing. Still to
   come: the actors (NPC moves and animations), speech and the other ops the scenes' scripts run. The trigger
   scripts behind scenes, doors and map links read and write in a C-like language
   ([docs/trigger-language.md](docs/trigger-language.md)), in a script editor (Edit > Trigger scripts) with a live
   check, completion, find and replace, renaming and lists of globals and symbols.
8. **Object viewer, browser and properties**: any object in its own 3D view; the objects around the selection or
   camera; an add-object tool that filters the types; finding objects by property, model or contents; and editing
   properties, both type-wide in `TYPES.DAT` (a sword's damage and damage type, health) and per object, every field
   with a tooltip.

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
| `src/U9.Mods` | Mod packages (`.u9mod`): deltas, executable patches, stacking, the installer, the catalog, U9GL downloads. |
| `src/U9ModManager` | The mod manager (Avalonia). |
| `src/U9ModManager.Cli` | `u9mod`: the mod manager on the command line. |
| `tests/U9.Formats.Tests` | Round-trip tests over every shipped file. |
| `tests/U9Editor.Core.Tests` | Picking and map-space tests. |
| `tests/U9.Mods.Tests` | Mods made, applied, stacked and taken back on copies of the game's files. |
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

Editor options: `--gamedata PATH`, `--map N`, `--size WxH`, `--top-down`, `--theme Light|Dark`, `--screenshot
out.png` (saves the first 3D frame and quits), `--window-shot out.png` (the whole window, panels included, rendered
off screen, then quits). In the view: left drag orbits, middle or Shift+drag pans, wheel zooms, W/A/S/D move, Q/E
turn, R/F raise and lower, Tab toggles the top-down map, Home frames the map, click selects a point or object (shown
in the status bar). The Links panel lists the current map's map changes; double-click goes to where one leads.

The tool bar left of the view sets what a left drag does: Select (1, the default: clicks select, Ctrl+click and
Ctrl+drag add to the selection, drags only move the camera), Move (2: drag the selection), Rotate (3: drag sideways
on the selection to turn it, a group about its middle), Place (4: every click on the ground places the type picked
in the Place tab, until Esc) and Terrain (5 or B: the brush). Below them, Orbit and Fly pick the camera (V switches):
flying, drags look round, W/A/S/D fly where you look, R/F go up and down, the wheel steps forward and Shift is
faster; the speed follows the height above the ground. View > Theme switches Light and Dark (remembered).

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
values (one undo step), Remove takes one off, and the row below adds one the object lacks (into a free place of its
last segment, or a new chained segment when that is full; such an object then stays in its 32-point chunk when moved).
Picking a property shows what its values mean, saying which meanings were seen in the shipped maps and which only go
by the name. Inside lists what a container holds or an NPC carries (with quantities); picking an item
shows its properties, to change say the coins in a bag. Below, the object's type: its TYPES.DAT values (hit points,
weight, volume, book, default model, usecode class and the flags), for every object of that type on every map; Set
type writes them as one undo step and Ctrl+S saves `static/TYPES.DAT` (first time keeping `.orig`). For anything with a Book Number (books, plaques, signs,
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

Prefabs (Prefabs tab, Ctrl+C / Ctrl+V): Ctrl+C copies the selected objects and the ground under them (their bounds
and a margin of points round them, 3 by default). Ctrl+V, here or after opening another map, shows the prefab's outline
on the ground under the mouse (its north side as copied in orange, its margin as an inner line); `[` and `]` turn it a
quarter, a click pastes it, again for another, and Esc stops. A paste is one undo step: the ground (heights, or
heights and textures; raised or lowered as a whole to meet the ground there, and faded into it across the margin, where
textures stop halfway), the objects (each keeps its height above the ground; with no ground pasted, optionally all by
one amount, as a building's parts must), and objects of the map already on the pasted ground going up and down with
it. Objects come whole: property chains (monster generators) and container contents are copied with fresh offsets, so
nothing points back into the source file. Links move together, keeping their offsets from each other, to numbers
neither used here nor within 15 of one (a script's `link + n` reach); unchanged when they are free, as on another map
often. Each script the objects run gets a copy in a new slot, its absolute links (`link = n`) moved too, except the
arrival of a map change, which names a link on the other map. Optionally the objects already in the area go. The
library keeps prefabs as `.u9prefab` files (JSON: the records, segments and contents, the ground and the scripts) in
`%AppData%/U9Editor/prefabs`. On the command line: `u9 prefab-copy <gamedata> <map> <x0> <row0> <x1> <row1>
<out.u9prefab> [--margin n] [--no-ground]` and `u9 prefab-paste <gamedata> <map> <x> <row> <in.u9prefab> [--turn k]
[--ground none|heights|textures] [--keep-heights] [--no-blend] [--keep-links] [--share-scripts] [--clear]
[--together]`.

Combat balance (Edit > Combat balance, Ctrl+B) opens a window over the game folder. Its Balance tab sets up the
Avatar (HP, strength, attack, armour per slot or a whole set, shield) and shows two heat maps: the mean number of hits
for the Avatar to kill each creature with each weapon (the best of the weapon's attacks, or one chosen), and the mean
number for each creature's attack to kill the Avatar in each armour set; hovering a cell shows the sums. The numbers
follow the game's rules (analysis by disassembly of the GOG `u9.exe`, checked by a second, independent pass): damage
is random(min, max) times the defender's multiplier for the attack's damage types, plus the Avatar's strength bonus
(0/2/4/8) and the Gauntlets of Fury's 5, minus the defender's defence roll (creatures: random(0, defence); the Avatar:
each armour piece's roll plus a tenth of the shield's). Blocking, poison and other on-hit effects, spells and the
per-object Min/Max Damage and Defense properties are not counted. The other tabs edit the tables: Weapons (damage, or
a shield's block roll, reach and hit radius), Armour, Creatures (NPC.FLX: HP, defence, kind; and the attack the balance
uses for each), Natural attacks (damage and damage types) and Damage multipliers (64 creature kinds by 11 damage types).
Ctrl+S saves: changes to `u9.exe`'s tables go to `u9.exe.patches.json` beside it, never into `u9.exe`; NPC.FLX is
written with an `.orig` copy (new games only: saves keep their own NPC records). Export changes as mod then carries the
patches, with notes such as "Longsword (5388): max damage 60 -> 80", and the mod manager writes them into
`u9-mod.exe`. Some damage multipliers are set by one-byte pushes in the start-up code; changing one of those rewrites
that function as a table copy (checked by emulating it). On the command line: `u9 combat`, `u9 combat-set`, `u9
combat-balance` and `u9 combat-exe` (see `u9 help`).

Dialogue (Edit > Dialogue, Ctrl+G) lists the 266 conversations of `static/text.flx` (one per source file of the
game's compiled conversation code) and their lines, with speaker and voice length; Ctrl+F searches every line by
text, clip name or number. What the game shows is the text.flx line, so editing it changes subtitles, reply choices
and journal entries; which lines exist and where replies lead is compiled code and stays as it is. While you type,
the editor shows what the game will make of the line: it converts it to the Windows code page and draws single
bytes with a 224-glyph font, so characters outside Windows-1252 become "?" or a near letter (no CJK), and €, Ž, ž, ©,
®, µ and ¶ have no glyph; it wraps the line as the 600 × 200 px subtitle box does (13 lines a page) and says how
long it stays (10 s a page without a voice; with one, until the sound ends, so a voiced line past 13 lines loses its
tail). Fix characters replaces U+0080-U+009F, which show as "?" (two shipped lines have U+0092 for an apostrophe).
▶ Play voice decodes the line's EA MicroTalk clip (a port of TITAN's decoder, see THIRD-PARTY-NOTICES.md); Save voice
as WAV exports it; Replace voice from WAV stores a WAV as uncompressed PCM (22,050 Hz 16-bit mono), which the game's
speech code accepts but no shipped clip uses, so check it in the game. Ctrl+S writes `text.flx` and, after a voice
replacement, `Speech.flx` (140 MB), each with a `.orig` copy. `u9 speech-wav <gamedata> <line> out.wav` decodes a
clip on the command line. How the game handles all this is written up in [docs/exe-research.md](docs/exe-research.md).
Vowel play (the Dialogue window's button) shuffles the vowels of every proper name, one new spelling per name used
everywhere ("Raven" might become "Rovon"), never a word the text already has: a quick test of text editing and a
bit of fun. The idea is "Vowel play" from Merrow, the Quest 64 randomizer by hangedmandesign
([github.com/hangedmandesign/merrow](https://github.com/hangedmandesign/merrow)), by way of the Ultima Underworld
editor. It changes only `text.flx`; voices keep the old names, and the NPC.FLX names the game looks lines up by stay.

Mods: save your changes (Ctrl+S), then File > Export changes as mod asks for a name, id, version and author and writes
a `.u9mod`: per changed file, the difference from the `.orig` copy the editor kept, tied to that original's SHA-256.
The U9 Mod Manager (`dotnet run --project src/U9ModManager`) adds game folders, imports mods into its library (in
`%AppData%/U9ModManager`), and applies the ones ticked, top first: files two mods both change are merged byte by byte
when their length stays, or record by record for FLX archives, and any clash (the same bytes, the same record, the
same executable bytes) refuses the whole set, naming it, with nothing written. Originals go into the manager's vault
first; "The original game" puts them back; a file changed since the manager wrote it is not overwritten without asking.
Executable patches (and the large-address flag) go into `u9-mod.exe`, which Play starts; `u9.exe` stays as shipped,
so U9GL's launcher, which recognises the game by its SHA-256, keeps working; U9GL's `dsound.dll` checks code in
memory rather than the file, so it should load into the copy too (not yet tried in the game). Patches touching the code U9GL checks before hooking the game are refused. The Catalog tab reads a JSON list
of mods from a URL and downloads them (checked against their SHA-256); the U9GL tab downloads U9GL's latest release,
checks it against the release's SHA256SUMS, unpacks its `u9gl` folder and opens U9GL's launcher to install it. `u9mod
help` lists the same on the command line (`make`, `import`, `plan`, `apply`, `original`, `status`, `recover`, `play`,
`catalog`, `download`, `u9gl`). Saved games keep their own copies of nonfixed objects and NPC records, so such changes
show in new games.

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

Scenes (Scenes tab): the map's in-engine scenes, each the chain of camera moves one object's script starts (on
Earth, map 14, the timer in the Avatar's house starts the opening: the camera cuts to point 71 outside, glides east
through 72 and 58 into the bedroom and settles on the bed at 59, 16 seconds). Play runs it in the view (camera points
hidden; Play again stops and puts the view back), and the slider scrubs through it. Clicking a move looks from its
camera point and selects it; its fields are the move's duration (half seconds), Glide (off: cut), Straight (off:
eased), Back (to the view before the scene) and which of the point's scripts run when it ends. Set move writes them
into the trigger script (every object running that script changes); Point from view moves the camera point to the
current view (the point looked at, heading, pitch and distance). Both are undo steps and Ctrl+S saves them. On the
command line, `u9 scenes <gamedata> <map>` lists the scenes and `u9 camera-point <gamedata> <map> <link> <x> <row>
<z> <heading> <pitch> [distance]` moves a camera point; `u9 objects <gamedata> <map> [fixed|nonfixed] [--type n]
[--props]` lists a map's objects.

Making scenes (Scenes tab, Make scenes): New scene here puts a trigger zone (a copy of one of the map's zones) at
the selected ground point; walking into it cuts the camera to a new camera point where the view is now. Add move
from view appends a new camera point where the view is, reached with the seconds, Glide and Straight of the move
fields. Orbit the selected object makes a scene that circles it (radius in points, height in object units above
its middle, the number of camera points and the seconds for a lap), each point looking at its middle, in straight
glides; its zone goes at the clicked ground point. The Script section shows the selected move's script (or the
scene's start) as text, one op a line (`op A B ; what it does`, the op in hex), and Apply script writes it back.
New scenes use new script slots and new objects (links above the map's highest), so shipped scenes stay as they
are; each is one undo step. `u9 orbit-scene <gamedata> <map> <x> <row> <z> <radius> <height> <points> <seconds>
<zone x> <zone row> [<zone z>]` does the same from the command line.

Start options for scripted pictures: `--look X,ROW --distance D --pitch P --yaw Y`, and `--scene N --scene-time S`
for the view of the map's scene N (as the Scenes tab lists them) at S seconds. `--prefab X0,ROW0,X1,ROW1` copies every
object in that rectangle of points (or `--prefab-file PATH` reads a saved prefab), then `--paste X,ROW[,TURNS]` pastes
it there or `--paste-preview X,ROW[,TURNS]` shows its outline; `--tab NAME` opens a side tab.
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
| Fixed / nonfixed objects (`fixed.N`, `nonfixed.N`) | yes | move, turn, copy, delete, place, paste whole trees (with undo) | all shipped files |
| Object types (`TYPES.DAT`, `TYPENAME.FLX`) | yes | TYPES.DAT fields (with undo) | unchanged when not edited |
| Models (`sappear.flx`) | yes (all 3,748 models, every LOD) | one limb, one LOD, from glTF (checked in the game) | — |
| Trigger scripts (`TRIGGERS.FLX`) | yes; map changes and camera moves understood | map-change scripts, camera moves | unchanged slots kept |
| Book texts (`BOOKS-EN.FLX`) | yes | yes (titles and texts) | all 460 records |
| Object properties (nonfixed property segments) | yes, named, chains followed, contents | values; add and remove | — |

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
- Trigger scripts (`TRIGGERS.FLX`, run by u9.exe's interpreter at 0x54F850 in the GOG exe): 6,708 scripts of 6-byte
  ops (u16 op, u16 A, u16 B) using about 90 commands. The op's high byte's low 5 bits minus 16 offset the script's
  current link (0: no link); A's low 13 bits are the object type (0x1FFF any). The current link starts as the
  triggering object's link; 0x11 sets it, 0x2C sets how far away targets may be (B x 128 object units, default 10
  points), 0x1F changes map. Most commands are sent to each object of the type and link in range as an event of its
  type's compiled usecode, with B: 0x01 activates (a camera point moves the camera; a timer starts), 0x28 makes the
  objects run their own script B & 3 (OO Trigger A, B, SF Trigger A, B) at once.
- Usecode: `TYPES.DAT`'s +4 is a type's usecode class; u9.exe binds event handlers to classes from 16 tables of
  {u16 class, u8 event, u32 handler} at 0x7A2488-0x7A9C30 (536 classes), falling back along the classes' chain.
  Camera points (type 2218) handle only event 2 (activate, 0x633410).
- Scenes: a camera point (type 2218, model 1025, an editor-only marker) is the point looked at (its z less 0x40),
  its rotation the view and its Misc Usecode 1 property (A) the distance in object units. The game reads the rotation
  through a matrix that is the transpose of the usual one (M01 = 2(xy + wz)), taken as Rz(a)·Rx(b)·Ry(c): heading
  a + 180° (fronts face south), pitch b, c ignored. Activated with B, it starts a camera process: duration (B &
  0x7FF) x 500 ms; 0x8000 glide from the current view (cosine-eased, 0x0800 straight), otherwise cut; 0x4000 glide
  back to the view before; when the time is up 0x2000 runs the point's first OO Trigger script, 0x1000 its second.
  A newer move replaces one under way; when the last ends the camera returns to the player. Checked in the game:
  desktop captures of the opening match the editor's frames (house, window, bedroom, bed) in path, timing and
  pitch, and point 71 moved up and tilted 35° down makes the scene open on the roof seen from above.
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
  records in it (those inside containers included, in 122 of the 123 pages that hold any) and (nonfixed) +0x18 its
  property segments (chained ones and those of contained records included, in every file). The u32 before the data is a free page list: the
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
