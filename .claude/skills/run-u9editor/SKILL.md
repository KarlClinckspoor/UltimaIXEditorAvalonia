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
| `down x y`, `up x y` | press / release the left button there; `move` in between, and `shot` mid-drag (a held terrain brush keeps working: `down 610 720`, `move 615 722`, wait 1.5 s, `shot`, `up 615 722` raises a hill under Moonglow's plants and shows them riding up) |
| `stop` | kills the editor (no unsaved-changes prompt) |

Editor arguments: `--gamedata PATH --map N --size WxH --look X,ROW --distance D --pitch P --yaw Y --top-down`.
Map 9 is Britannia (Lord British's throne at `--look 502,554`), 4 is Moonglow (small, quick to load).

Layout at `--size 1400x900` (window 1416 x 939 at 100% scaling): the 3D view is x 8-1028, y 104-905; the
Selected object panel is x 1030-1416, y 105-285 (X field at 1120,165); the status bar is y 905-935, with the
last edit message in yellow on the left and the hover readout after it.

## Screenshot without the mouse

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
59776,64640. Zones can sit well below the ground you see (that one at z 2137 under ground at 2710): teleport
into them with an explicit z rather than walking. Read the position with `pos` before and after.

- The game draws in U9GL's own 1280 x 960 window at 0,0. Desktop captures of it come out white or black: use
  `game.ps1 shot` (Ctrl+P; U9GL saves `gamedata/u9-<n>-after.png`, which the driver moves to `%TEMP%`).
- Keys reach the game with `keybd_event` and scan codes from `MapVirtualKey` (arrows need the extended flag);
  `game.ps1` does that. The game draws its own mouse cursor, so mouse clicks miss: use keys (`game.ps1 quit` is
  Ctrl+X, then Enter on the dialog).
- `start_at` on Moonglow (map 4) makes the game shut down (cleanly, no crash report) during the teleport from the
  new game's Earth (map 14), with or without a height. Starting on Britannia and then `tppos 4 ...` keeps the game
  running, but there the picture stayed dark and unchanging and the console stopped answering. Britannia (map 9)
  is the dependable test map for now.
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

## Troubleshooting

- `error MSB3027: Could not copy ... U9Editor.exe ... The file is locked by: "U9Editor (pid)"` → the editor is
  running: `driver.ps1 stop`, then build.
- `Exception calling "Clone" with "2" argument(s): "Out of memory."` from `shot` with a rectangle → the rectangle
  runs outside the window (System.Drawing reports it as out of memory). Take a full `shot` and check the size.
- `No U9Editor window: run 'launch' first` → the editor exited or was closed; `launch` again.
- `... not found: dotnet build src/U9Editor.App first` → build before `launch`.
