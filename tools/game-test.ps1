# Puts a locally built, patched U9GL (branch spawn-cheats of tgwUltima9GL, built with its build.sh) into
# the game folder for a test run, starting at a map point with the game's own fly and invulnerability
# cheats on; -Uninstall takes it all out again and puts Options.ini back.
#
#   powershell -ExecutionPolicy Bypass -File tools/game-test.ps1 -Map 9 -X 467 -Row 505 [-Height 600] [-Run]
#   powershell -ExecutionPolicy Bypass -File tools/game-test.ps1 -NoStart -NoFly -NoGod -Run   (a plain new game: its opening scene on Earth)
#   powershell -ExecutionPolicy Bypass -File tools/game-test.ps1 -Uninstall
#
# X and Row are terrain points, as the editor shows them; Height (object units) is optional: without it the
# Avatar lands on the terrain (which can be a riverbed or under a castle floor). In the game, Ctrl+P saves the
# frame as u9-<n>-after.png in the game folder, the console (Ctrl+`) takes "fly", "god", "pos" and
# "tppos <map> <x> <y> [<z>]", and U9GL logs to %TEMP%\u9log.txt ("CONSOLE start: ..." lines). The key map gets
# ctrl+h = pass_one_hour, another of the game's own cheats, for daylight (default.kmp is put back too).
# A new game starts on Earth (map 14), and going from there straight to Moonglow (map 4) shuts the game down: start
# on Britannia and use tppos for other maps.
param(
    [int]$Map = 9, [double]$X = 467, [double]$Row = 505, [int]$Height = -1,
    [switch]$NoFly, [switch]$NoGod, [switch]$NoStart, [switch]$Run, [switch]$Uninstall,
    [string]$GameData = "gamedata", [string]$U9gl = "..\u9gl-src\u9gl\out"
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path $GameData).Path
$backup = Join-Path $game "u9gl-test.backup"

if ($Uninstall) {
    Get-Process u9 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Remove-Item (Join-Path $game "dsound.dll") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $game "u9gl") -Recurse -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $backup "Options.ini")) {
        Copy-Item (Join-Path $backup "Options.ini") (Join-Path $game "Options.ini") -Force
        if (Test-Path (Join-Path $backup "default.kmp")) { Copy-Item (Join-Path $backup "default.kmp") (Join-Path $game "default.kmp") -Force }
        Remove-Item $backup -Recurse
    }
    "U9GL taken out; Options.ini and default.kmp put back."
    return
}

$out = (Resolve-Path $U9gl).Path
foreach ($f in "u9log.dll", "dsound.dll", "u9gl.ini") { if (-not (Test-Path (Join-Path $out $f))) { throw "$out has no ${f}: build U9GL first (u9gl/build.sh)" } }
if ((Test-Path (Join-Path $game "dsound.dll")) -and -not (Test-Path $backup)) { throw "There is already a dsound.dll beside u9.exe that this script did not put there." }

if (-not (Test-Path $backup)) { New-Item -ItemType Directory $backup | Out-Null; Copy-Item (Join-Path $game "Options.ini"), (Join-Path $game "default.kmp") $backup }
New-Item -ItemType Directory -Force (Join-Path $game "u9gl") | Out-Null
Copy-Item (Join-Path $out "u9log.dll"), (Join-Path $out "dsound.dll") (Join-Path $game "u9gl") -Force
Copy-Item (Join-Path $out "dsound.dll") $game -Force
Copy-Item (Join-Path $out "u9gl.ini") (Join-Path $game "u9gl\u9gl.defaults.ini") -Force

$start = "$Map,$([int][Math]::Round($X * 128)),$([int][Math]::Round($Row * 128))" + $(if ($Height -ge 0) { ",$Height" } else { "" })
if ($NoStart) { $start = "" }     # U9GL ignores an empty start_at: the new game plays its own opening on Earth
# what U9GL's launcher sets for its OpenGL renderer in its own window, then the test's own keys (later lines win)
$ini = (Get-Content (Join-Path $out "u9gl.ini") -Raw) + @"

# ---- set by tools/game-test.ps1 ----
rasterizer5 = 1
own_renderer_life = 1
glide_present = 0
gl_detach = 1
loader = 1
loader_dll = u9log.dll
depth_mode = 2
gl_window_w = 1280
gl_window_h = 960
gl_window_x = 0
gl_window_y = 0
gl_screenshot = 2
start_at = $start
start_fly = $(if ($NoFly) { 0 } else { 1 })
start_god = $(if ($NoGod) { 0 } else { 1 })
"@
Set-Content (Join-Path $game "u9gl\u9gl.ini") $ini -Encoding ascii

# the game's own hour cheat on ctrl+h, under the key map's empty [CHEAT COMMANDS] section
$kmp = [IO.File]::ReadAllText((Join-Path $backup "default.kmp"))
if ($kmp -notmatch 'pass_one_hour') { $kmp = $kmp -replace '\[CHEAT COMMANDS\]\r?\n', "[CHEAT COMMANDS]`r`nctrl+h = pass_one_hour`r`n" }
[IO.File]::WriteAllText((Join-Path $game "default.kmp"), $kmp)

$opts = Get-Content (Join-Path $game "Options.ini")
$opts = $opts -replace '^Game Rasterizer=.*', 'Game Rasterizer=3' -replace '^GameScreenDepth=.*', 'GameScreenDepth=16'
Set-Content (Join-Path $game "Options.ini") $opts -Encoding ascii
"U9GL put in; a new game or a loaded save starts at map $Map ($X, $Row) = start_at $start$(if (-not $NoFly) { ', flying' })$(if (-not $NoGod) { ', invulnerable' })."

if ($Run) {
    Remove-Item "$env:TEMP\u9log.txt" -ErrorAction SilentlyContinue
    Start-Process -FilePath (Join-Path $game "u9.exe") -WorkingDirectory $game
    "Started u9.exe. Esc skips the intro; watch %TEMP%\u9log.txt for 'CONSOLE start'."
}
