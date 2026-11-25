---
name: run-u9editor
description: Build, run, drive and screenshot the U9 Editor (Ultima IX map editor, Avalonia + OpenGL) on Windows. Use to launch the app, click and drag objects, press its keys, take window screenshots, check a rendering or editing change in the real app, or run the tests.
---

# Run the U9 Editor

A Windows desktop app (C# .NET 10, Avalonia 12, OpenGL through ANGLE). Agents drive it with
`.claude/skills/run-u9editor/driver.ps1`, which starts the editor, moves the real mouse, sends keys and captures
the window to PNG through user32. Paths below are relative to the repo root. It runs on the user's live desktop:
**ask them to keep off the mouse and keyboard while you drive**, or their input mixes with yours.

## Prerequisites

- Windows with the .NET 10 SDK (`dotnet --version` → 10.x).
- The game data: a copy of the Ultima IX install in `gamedata/` (holding `static/` and `runtime/`). Not in git.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .claude/skills/run-u9editor/driver.ps1 stop
dotnet build U9Editor.sln -v q -nologo
```

Stop the editor first: a running `U9Editor.exe` is locked and the build fails (see Troubleshooting).

## Run (agent path): the driver

```powershell
$s = ".claude/skills/run-u9editor/driver.ps1"
powershell -ExecutionPolicy Bypass -File $s launch --gamedata gamedata --map 4 --size 1400x900 --look 257,230 --distance 30 --pitch 50
powershell -ExecutionPolicy Bypass -File $s shot "$env:TEMP\u9_window.png"
powershell -ExecutionPolicy Bypass -File $s click 893 360
powershell -ExecutionPolicy Bypass -File $s shot "$env:TEMP\u9_panel.png" 1030 105 386 180
powershell -ExecutionPolicy Bypass -File $s drag 893 375 600 520
powershell -ExecutionPolicy Bypass -File $s key "^z"
powershell -ExecutionPolicy Bypass -File $s move 520 560
powershell -ExecutionPolicy Bypass -File $s shot "$env:TEMP\u9_status.png" 0 905 1416 30
powershell -ExecutionPolicy Bypass -File $s stop
```

That opens Moonglow (map 4) over the Lycaeum gate, selects the tree to the right of it (fixed #1110, type 2359),
drags it onto the road, undoes the drag and reads the status bar. Read every PNG you take.

Terrain brush: `key "b"` turns it on (Terrain tab, Raise), then `drag 420 650 600 560` raises a ridge across the
road in the view above (the title gains ` *`, the status bar says `Terrain: raise`), `key "^z"` takes it back. The
tool list is the combo box at 1135,351; with it open, Paint texture is at 1135,493; the Texture field is at
1245,495 (`click 1245 495` then `key "^a11{TAB}"`). On Britannia `--look 440,540 --distance 40 --top-down` shows
open grass at the upper left to paint into (`drag 120 380 250 420`); the panel names what the texture blends into. `key "{]}"` widens the brush by a quarter, `click 500 700 ctrl` takes
the ground's texture (Paint) or height (Flatten) under that pixel. Below the check boxes: Water height (field at 1148,604:
`click 1148 604` then `key "^a440{ENTER}"` floods Moonglow's gate road) and the texture palette (first row of
thumbnails at y 645, 40 px apart from x 1055). A drag of that length at
strength 20 builds a hill some 45 units high: the default (6) is gentler.

| Command | Does |
|---|---|
| `launch [editor args]` | starts `src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe`, parks the window at 0,0, waits 10 s for the map and models |
| `shot <png> [x y w h]` | captures the window, or a rectangle of it |
| `click x y [ctrl\|shift]`, `drag x1 y1 x2 y2`, `move x y` | mouse, in window pixels as a full `shot` shows them (title bar included) |
| `key <SendKeys>` | `^z` undo, `^y` redo, `^d` copy, `^s` save, `{DEL}`, `{END}` to ground, `{[}` / `{]}` turn, `{PGUP}` / `{PGDN}`, `{ESC}` |
| `drag x1 y1 x2 y2 ctrl` | box selection: at the `launch` view above, `drag 130 180 340 240 ctrl` selects the row of ten small trees left of the gate (they turn magenta); then `drag 230 212 480 520` moves all ten, `{]}` turns them about their middle, `^d` copies them |
| `dclick x y` | double click: list items (a link, a found object, a bookmark). The side tabs are at y 306 with nothing selected, y 319 with an object selected (Links 1057, Place 1104, Contents 1163, Find 1219, Scenes 1269, Terrain 1327); on Find the search box is at 1150,351 (`key "telescope{ENTER}"`), All maps at 1281,351, Go to at 1147,699, the first bookmark at 1100,761. `key "^aplaque nonfixed{ENTER}"`, `dclick 1150 402`, `click 1163 306` shows Sir Dupre's tomb plaque and its book 407 on the Contents tab (text box at 1200,650, Set text at 1066,740) |
| `down x y`, `up x y` | press / release the left button there; `move` in between, and `shot` mid-drag (a held terrain brush keeps working: `down 610 720`, `move 615 722`, wait 1.5 s, `shot`, `up 615 722` raises a hill under Moonglow's plants and shows them riding up) |
| `stop` | kills the editor (no unsaved-changes prompt) |

Editor arguments: `--gamedata PATH --map N --size WxH --look X,ROW --distance D --pitch P --yaw Y --top-down`.
Map 9 is Britannia (Lord British's throne at `--look 502,554`; the real city of Moonglow is in its east). Map 4,
named "Moonglow", is a development leftover the game does not use: handy for UI tests because it is small and
quick to load, but check anything game-facing on Britannia.

Layout at `--size 1400x900` (window 1416 x 939 at 100% scaling) since the tool bar and the compact theme
(2026-11-24), read from a `--window-shot` (client pixels; window pixels are about 8 more in x and 31 more in y, so
check with a full `shot` before clicking): the tool bar is x 0-37 (Select, Move, Rotate, Place, Terrain at y 72,
104, 137, 170, 202; Orbit and Fly at 244, 276); the 3D view x 37-1020, y 55-878; the Selected object panel x
1022-1400 from y 60 (X field at 1110,110); the side tabs at y 219 with nothing selected (Links 1050, Place 1099,
Contents 1161, Find 1220, Scenes 1273, Terrain 1334); the status bar y 880-900. The figures elsewhere in this file
(tabs at y 306, the Scenes rows) are from the older, roomier layout: measure again before relying on them.
A drag on an object only moves it in Move mode (key 2); in the default Select mode it orbits.

## Screenshot without the mouse

The whole window, panels and all, without touching the desktop (the 3D frame is drawn over the compass):

```powershell
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 14 --size 1400x900 --theme Dark --scene 4 --scene-time 9 --window-shot $env:TEMP\u9_window.png"
```


For a picture of the 3D view only (no menus or panels), the editor can save its first frame and quit:

```powershell
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 9 --size 1400x900 --look 467,505 --distance 22 --pitch 50 --screenshot $env:TEMP\u9_view.png"
```

Use this to check rendering (terrain textures, models) without touching the desktop. Keep `Start-Process -Wait`:
PowerShell does not wait for a GUI program started directly, so the PNG would not exist yet on the next line.
That spot (467, 505 on Britannia) shows a road, a bridge and river banks, which go jagged if terrain texture
rotation breaks.

## Saving touches the game data

Ctrl+S writes `gamedata/static/fixed.N` / `gamedata/runtime/nonfixed.N` and, the first time, keeps the original as
`.orig`. To check saving and leave the data as it was:

```powershell
Copy-Item gamedata\static\fixed.4 "$env:TEMP\fixed.4.mine"
# ... edit in the editor, then: powershell -ExecutionPolicy Bypass -File $s key "^s"
powershell -ExecutionPolicy Bypass -File $s stop
Copy-Item "$env:TEMP\fixed.4.mine" gamedata\static\fixed.4 -Force
Remove-Item gamedata\static\fixed.4.orig
```

## Test in the game itself

`tools/game-test.ps1` puts a patched U9GL into `gamedata/` so a new game starts at a map point, flying (through
walls and ground) and invulnerable, using the game's own developer cheats. It needs the U9GL clone at
`../u9gl-src` on branch `spawn-cheats`, built with MSYS2's 32-bit GCC (`cd ../u9gl-src/u9gl && ./build.sh`).

```powershell
powershell -ExecutionPolicy Bypass -File tools/game-test.ps1 -Map 9 -X 467 -Row 505 -Run
# ...Esc three times skips the intro; U9GL logs "CONSOLE start: arrived ..." to %TEMP%\u9log.txt
powershell -ExecutionPolicy Bypass -File tools/game-test.ps1 -Uninstall
```

Drive the running game with `.claude/skills/run-u9editor/game.ps1`:

```powershell
$g = ".claude/skills/run-u9editor/game.ps1"
powershell -ExecutionPolicy Bypass -File $g tap esc          # three times, 2 s apart: the intro, then a new game starts
powershell -ExecutionPolicy Bypass -File $g shot             # Ctrl+P in the game -> prints %TEMP%\u9game-<n>.png
powershell -ExecutionPolicy Bypass -File $g hold up 2500     # walk forward 2.5 s
powershell -ExecutionPolicy Bypass -File $g tap ctrl+backquote p o s enter     # U9GL console: where am I?
powershell -ExecutionPolicy Bypass -File $g tap t p p o s space 9 space 7 3 3 4 3 space 3 3 6 0 0 space 2 1 6 0 enter
powershell -ExecutionPolicy Bypass -File $g log 15           # console output is in the log as "CONSOLE ..." lines
```

Checked that way: a link made with the editor (the cave at 573, 262.4 on Britannia retargeted to a new arrival
at the bridge, saved) works in the game. `tppos` into the zone sent the Avatar to the arrival, 59779,64640 for
59776,64640. Terrain at Moonglow (around 744, 396 on Britannia: start at 738, 382,
then 	ppos 9 95360 50432 3300 puts the camera over the hill) checked the same way: objects that followed a raised hill
sit on it, painted sand blends into grass. A console command typed while the console is closed after a teleport
needs Ctrl+ first; if log shows no >  line for it, the console was closed: open it and type again.
Zones can sit well below the ground you see (that one at z 2137 under ground at 2710): teleport
into them with an explicit z rather than walking. Read the position with `pos` before and after.

- The game draws in U9GL's own 1280 x 960 window at 0,0. Desktop captures of it come out white or black: use
  `game.ps1 shot` (Ctrl+P; U9GL saves `gamedata/u9-<n>-after.png`, which the driver moves to `%TEMP%`).
- Keys reach the game with `keybd_event` and scan codes from `MapVirtualKey` (arrows need the extended flag);
  `game.ps1` does that. The game draws its own mouse cursor, so mouse clicks miss: use keys (`game.ps1 quit` is
  Ctrl+X, then Enter on the dialog).
- `start_at` on map 4 (the unused development "Moonglow", not the city) makes the game shut down (cleanly, no crash report) during the teleport from the
  new game's Earth (map 14), with or without a height. Starting on Britannia and then `tppos 4 ...` keeps the game
  running, but there the picture stayed dark and unchanging and the console stopped answering. Britannia (map 9)
  is the dependable test map for now.
- `tppos` to 63310, 56600 (Britain's archery yard fence) left the console unanswering with or without edited
  files; start at the spot with `game-test.ps1` instead of teleporting there.
- Daylight: `game.ps1 hours 5` passes five hours with the game's own `pass_one_hour` cheat, which
  `tools/game-test.ps1` binds to Ctrl+H in `default.kmp` (restored by `-Uninstall`). The console's `sun` prints the
  time (`sun at 06:23 ...`; a new game on Britannia starts about then). Checked: 06:24 + 5 = 11:26.
- The console stays open after a command; a teleport closes it. Typing goes to the console while it is open.
- A new game copies `runtime/nonfixed.N`: back up the files the editor saves (and delete their `.orig`) to put
  the game folder back afterwards.
- When another window takes the focus the game minimises and U9GL hides its window: `ShowWindow(game, 9)` and
  `SetForegroundWindow` bring both back.
- Without U9GL, the same cheats work from the key map: under `[CHEAT COMMANDS]` in `gamedata/default.kmp`,
  `ctrl+y = toggle_avatar_fly` (checked: he flies through the house wall), `ctrl+u = toggle_avatar_invulnerable`.
- Always `-Uninstall` afterwards, and put back any `default.kmp` edit: the game folder is the user's copy.

## Trigger script editor

Edit > Trigger scripts (Ctrl+T) opens it; the language is in `docs/trigger-language.md`. Without the desktop (a
locked screen too), start options open it, put text in it, show a side list and save the window:

```powershell
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 9 --size 1400x900 --scripts 2551 --scripts-side symbols --scripts-shot $env:TEMP\u9_se.png"
```

`--scripts N` opens script N (0: none), `--scripts-text FILE` types that file's text into it (it is checked:
errors underlined and listed), `--scripts-side globals|symbols` opens a side list, `--scripts-shot PNG` saves the
editor window and quits. Script 2551 has labels, a tint test and dimmed `[A=...]` fields; 584 sets globals and
plays the Ambush movie. `dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll script gamedata 584` prints a script.
Apply, New and Delete are undo steps of the open map, saved by Ctrl+S into `static/TRIGGERS.FLX` (keeping
`.orig`): put the file back after a test. The completion popup and F2 need the live window (driver keys).

## Scenes

The Scenes tab (at y 306 / 319 like the others, x 1269 with an object selected) lists the map's scenes; on Earth
(`--map 14`) the opening is the fifth (`click 1200 418` on the first row, then `key "{DOWN}{DOWN}{DOWN}{DOWN}"`).
Clicking a move (rows 36 px apart from y 620) looks from its camera point; then the Seconds box is at 1122,768
(`key "^a8{ENTER}"` sets the move), Play at 1057,553. A mouse-free picture of a scene moment:

```powershell
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 14 --size 1400x900 --scene 4 --scene-time 15.9 --screenshot $env:TEMP\u9_scene.png"
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll scenes gamedata 14      # the same scenes as text
```

The game's own opening, captured: `tools/game-test.ps1 -NoStart -NoFly -NoGod -Run` (an empty start_at: no
teleport), wait 12 s, `game.ps1 tap esc` once (it skips the intro movie; the scene follows at once, and a second Esc
interrupts it), then capture the desktop every 0.4 s with `System.Drawing.Graphics.CopyFromScreen(0,0,0,0,1280x960)`:
U9GL's Ctrl+P writes nothing while the scene plays, but desktop captures of the game window work here. The scene
fades in about 1 s after the Esc and ends about 16 s later. `u9 camera-point ...` edits `runtime/nonfixed.14`,
which a new game reads: back it up first and put it back (and delete the `.orig`) afterwards.

## Models and Blender

Blender 5.2 is at `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe` on this machine; it runs headless:

```powershell
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll model-export gamedata 23 $env:TEMP\barrel.glb
& "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup --python script.py -- $env:TEMP\barrel.glb $env:TEMP\out.glb
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll model-info $env:TEMP\out.glb
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll model-import gamedata 23 $env:TEMP\out.glb   # keeps static\sappear.flx.orig
```

The Blender script imports with `bpy.ops.import_scene.gltf`, edits (scale, then `transform_apply`), and exports with
`bpy.ops.export_scene.gltf(filepath=..., export_format='GLB', export_extras=True)`: without `export_extras` the
materials lose the u9 texture numbers. Blender keeps the colour property as a string (it exceeds a 32-bit int); the
importer reads either. Afterwards put `sappear.flx.orig` back over `sappear.flx` and delete it. In the game, Britain's
outdoor barrels (model 23) are at 494.6, 444.6: `tools/game-test.ps1 -Map 9 -X 492 -Row 445 -Height 2420` starts in
the archery yard beside them (without -Height the Avatar can land under a house floor), and the start view after
`hours 5` and `tap ctrl+delete` shows them at the left edge.

## Prefabs

Ctrl+C copies the selection with its ground; Ctrl+V shows the outline under the mouse, `[` / `]` turn it, a click pastes,
Esc stops (the Prefabs tab holds the options and the library). Mouse-free pictures, copying the objects round Moonglow
on Britannia and pasting them onto map 4 turned a quarter:

```powershell
$pf = "$env:TEMP\moonglow.u9prefab"
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll prefab-copy gamedata 9 738 390 750 402 $pf
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 4 --size 1400x900 --look 240,200 --distance 30 --pitch 55 --tab Prefabs --prefab-file $pf --paste 233,193,1 --screenshot $env:TEMP\u9_paste.png"
```

`--prefab X0,ROW0,X1,ROW1` copies on the start map instead of `--prefab-file`; `--paste-preview X,ROW[,TURNS]` shows
only the outline (with `--window-shot`, the Prefabs tab beside it). Nothing is saved; scripted shots close without the
unsaved-changes question. `u9 prefab-paste` does write the map's files and TRIGGERS.FLX: back them up first, put them
back and delete the `.orig` files afterwards. Britannia has hardly any flat, empty ground; map 4 round 240, 200 is
grass and a road, handy for seeing a paste.

## Mod manager

`src/U9ModManager` (the app) and `u9mod` (`dotnet src/U9ModManager.Cli/bin/Debug/net10.0/u9mod.dll help`). Never
point them at `gamedata/` with a real home: test in a temp game folder (copies of `u9.exe` and the files a mod
touches) with `U9MM_HOME` (CLI) or `--home` (app) set to a temp folder. The app takes `--game PATH --home PATH --shot
out.png` (saves the window and quits). `tests/U9.Mods.Tests` builds such folders itself (`TempGame`).

## Combat balance

Edit > Combat balance (Ctrl+B) opens `CombatWindow`. A mouse-free picture of a tab:

```powershell
Start-Process -Wait -FilePath src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe -ArgumentList "--gamedata gamedata --map 4 --size 1400x900 --combat balance --combat-armour 3 --combat-shot $env:TEMP\u9_combat.png"
```

`--combat` takes balance, balance-you (the "them to kill you" map), weapons, armour, creatures, natural or
multipliers; `--combat-armour 0-6` wears a set (none, leather, chain, plate, bone, wyrmguard, blackrock). The CLI does
the same without a window: `u9 combat <game>` prints the tables and pending patches, `u9 combat-set <game>
weapon:5388.max=80 mult:31.cold=0.75 npc:47.hp=150 ...` edits and saves, `u9 combat-balance <game> --weapon 5388
--armour 3`, `u9 combat-exe <game> out.exe` writes the patched image elsewhere. Saving writes
`u9.exe.patches.json` and `runtime/NPC.FLX` (+ `.orig`): do it in a temp game folder (copies of `u9.exe`,
`runtime/NPC.FLX`, `static/TYPENAME.FLX`), never in `gamedata/`. To check a multiplier change really runs, emulate
the start-up code (VA 0x44A040) of the written exe with unicorn (`uv run --with pefile --with unicorn python ...`, as
`analysis/item-stats/emu_resist.py` does) and compare the 704 floats at VA 0x84E598.

## Dialogue

Edit > Dialogue (Ctrl+G) opens `DialogueWindow`. Mouse-free pictures: `--dialogue-line 4257 --dialogue-shot out.png`
(a line with a U+0092 apostrophe: shows the warning, Fix characters and the wrap preview) or `--dialogue-find glyph
--dialogue-shot out.png`. Saving writes `static/text.flx` and, after Replace voice from WAV, `sound/Speech.flx`
(140 MB) with `.orig` copies: test in a temp folder, never `gamedata/`. `u9 speech-wav gamedata 6 out.wav` decodes
a clip (the decoder matches TITAN's bit for bit: `uv run --with titan-ultima --with numpy` can check). Playback
(PlaySound) is Windows only; nobody can hear it in a scripted run, so ask the user to listen.

## Watching the game's mechanics live (u9mon)

`tools/u9mon.py` hooks u9.exe with Frida (no file changes; the exe always loads at 0x400000, so the decompile's
addresses are live addresses). It prints:
- HP (and mana) losses, with the damage path that caused them;
- spell casts, with the spell number;
- creatures fighting and changing target, and the team (friend) test per pair;
- trigger scripts: each run (object, which script, slot), every op with the flag it left, commands sent to objects,
  and the helpers behind the commands still marked **?**.

It can also travel and run scripts. The addresses and how they were found are in `docs/exe-research.md` section 6.

```powershell
uv run --with frida python tools/u9mon.py --wait --log $env:TEMP\u9mon.jsonl --commands $env:TEMP\u9cmds.txt
powershell -ExecutionPolicy Bypass -File tools/keep-awake.ps1 -Minutes 120        # stops Windows locking meanwhile
powershell -ExecutionPolicy Bypass -File tools/arena-test.ps1 -Test 02-crab-vs-hound -Run   # a game; closes after 5 min
dotnet src/U9Editor.Cli/bin/Debug/net10.0/u9.dll script-uses gamedata 3E --map 9  # scripts with command 3E + tp/run lines
Add-Content $env:TEMP\u9cmds.txt "tp 9 64516,67498,2599"                          # then, once it says RESULT tp:
Add-Content $env:TEMP\u9cmds.txt "run 2121 3076 1962 2535 666 0"
```

- **Finding places:** `u9 script-places gamedata 3E 10 ... [--map N] [--b-mask FF00]` lists where commands run with
  nobody using anything. It starts from trigger zones, puzzle timers, proximity objects and patrol markers, follows
  `28` calls, and gives the globals to set first. The curated list for the unknown commands is
  `analysis/script-tests/README.md` (private).
- **Commands:** `where`, `global <n> [value]`, `tp <map> x,y[,z] [yaw]`, `find <type> x y z link` and
  `run <type> x y z link which [label]`.
  They come from stdin or from lines appended to the `--commands` file, and run between frames on the game's thread.
  `x y z` for find and run are the record's in-chunk values, which `script-uses` prints ready to paste.
- **Checked on 2026-11-25:** `where`, a `tp` from the arena (map 91) to Britannia, and `run` of script 1814 (command
  3E: helper 0x5B7220 got B = 6 and returned 1).
- **`run` only finds objects on the current map,** so `tp` there first.
- **Repeats are shown once.** A script that runs again the same way (some run every frame) prints a single
  "ran again the same way" line.
- **`--wait`** attaches as soon as the game starts, and the monitor quits when the game does.
- When you wait on its output from Bash, name the monitor's own task output file. "Newest file" picks your waiting
  command's file instead.

## Run (human path)

`dotnet run --project src/U9Editor.App -- --gamedata gamedata --map 9` opens the window; close it to stop.

## Test

```powershell
dotnet test U9Editor.sln -nologo
```

About 600 tests, half a minute. They need `gamedata/` (or `U9_GAMEDATA`) and skip without it.

## Gotchas

- **The window cascades.** Each new window opens further down and right; a few launches in, the status bar sits
  under the taskbar and `shot` shows the taskbar instead. `launch` moves the window to 0,0 for that reason.
- **Somebody else's input.** The driver moves the real cursor. If the view jumps, the window maximises or the
  selection changes between your steps, the user is using the PC: stop and ask.
- **Grabbing an object:** a drag moves the object only if it starts on the *selected* object (anywhere within
  its bounds). Click first, then drag; a drag that starts elsewhere orbits the camera.
- **Keys in panel fields:** while a panel text box has focus, Ctrl+Z/Y/D and the edit keys go to the text box.
  Ctrl+S still saves.
- **SendKeys syntax:** brackets must be braced (`{[}`), Ctrl is `^`, Enter `{ENTER}`; `^a270{ENTER}` replaces a
  field's text and applies it.
- **Waiting:** `launch` waits 10 s; Britannia (33k objects, 2,170 models) is ready in about 8. Edits redraw within
  half a second; sleep ~500 ms before a `shot`.
- **The pink tint** on a selected model is the selection highlight, not a texture fault.
- **A locked screen.** After some 15 minutes away Windows locks the session: `shot` returns the wallpaper,
  `CopyFromScreen` throws "The handle is invalid", keys sent to the game never arrive (its log shows the foreground
  as `Windows.UI.Core.CoreWindow`), and Git Bash may refuse to start (EPERM). Use `--window-shot` / `--screenshot`
  for the editor and leave in-game checks until the user is back.

## Troubleshooting

- `error MSB3027: Could not copy ... U9Editor.exe ... The file is locked by: "U9Editor (pid)"` → the editor is
  running: `driver.ps1 stop`, then build.
- `Exception calling "Clone" with "2" argument(s): "Out of memory."` from `shot` with a rectangle → the rectangle
  runs outside the window (System.Drawing reports it as out of memory). Take a full `shot` and check the size.
- `No U9Editor window: run 'launch' first` → the editor exited or was closed; `launch` again.
- `... not found: dotnet build src/U9Editor.App first` → build before `launch`.
