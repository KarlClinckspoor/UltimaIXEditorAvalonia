# Puts a test arena into the game folder: a flat new map 91 with creatures set against each other (each its own
# team through its link number), the Avatar in a small box of walls and out of their target lists, plus whatever
# data edits that test makes. A new game starts on the arena through the game's own developer start option
# ([Game] Start= in Options.ini). -Uninstall puts everything back.
#
#   powershell -ExecutionPolicy Bypass -File tools/arena-test.ps1                       # list the tests
#   powershell -ExecutionPolicy Bypass -File tools/arena-test.ps1 -Test 02-crab-vs-hound -Run
#   powershell -ExecutionPolicy Bypass -File tools/arena-test.ps1 -Uninstall
#
# -Run starts u9.exe and waits: after -Minutes (default 5) it closes the game, and either way, once the game has
# exited, it takes the arena out again (-Keep leaves it in for another run).
#
# Each test is a folder in analysis/arena-tests (local only: they hold game data; its README says what each one
# does and how it went). Originals go to <game>/_arena_backup.
param([string]$Test = "", [string]$Game = "", [switch]$Run, [double]$Minutes = 5, [switch]$Keep, [switch]$Uninstall)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Game) { $Game = Join-Path $repo "gamedata" }
$Game = (Resolve-Path $Game).Path
$tests = Join-Path $repo "analysis\arena-tests"
if (-not $Uninstall -and -not $Test) {
    "Tests in ${tests}:"
    Get-ChildItem -LiteralPath $tests -Directory | Where-Object { Test-Path (Join-Path $_.FullName "arena.ini") } | ForEach-Object { "  $($_.Name)" }
    "Install one with -Test <name> (-Run also starts the game)."
    return
}
$stage = Join-Path $tests $Test
$backup = Join-Path $Game "_arena_backup"
$replaced = @("runtime\NPC.FLX", "static\sappear.flx", "sound\SFXTMPL.FLX", "Options.ini")
$added = @("static\terrain.91", "static\fixed.91", "runtime\nonfixed.91", "arena.ini")
$latin1 = [Text.Encoding]::GetEncoding(28591)

function Find-File([string]$rel) {
    # the game folder's own spelling of a file (static\SAPPEAR.FLX ...), or the path as given
    $dir = Join-Path $Game (Split-Path $rel -Parent)
    $name = Split-Path $rel -Leaf
    $hit = Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
    if ($hit) { return $hit.FullName } else { return (Join-Path $Game $rel) }
}

if ($Uninstall) {
    if (-not (Test-Path $backup)) { "Nothing to undo: no $backup."; return }
    foreach ($rel in $replaced) {
        $b = Join-Path $backup $rel
        if (Test-Path $b) { Copy-Item $b (Find-File $rel) -Force }
    }
    foreach ($rel in $added) { $p = Find-File $rel; if (Test-Path $p) { Remove-Item $p -Force } }
    # files the arena run left in savegame (a new game writes nonfixed.*, start.dat ...), not the ones there before
    $before = @()
    $list = Join-Path $backup "savegame-before.txt"
    if (Test-Path $list) { $before = [IO.File]::ReadAllLines($list) }
    $sg = Join-Path $Game "savegame"
    if (Test-Path $sg) {
        Get-ChildItem -LiteralPath $sg -File | Where-Object { $before -notcontains $_.Name } | ForEach-Object { Remove-Item $_.FullName -Force; "removed savegame\$($_.Name)" }
    }
    Remove-Item $backup -Recurse -Force
    "Arena taken out; NPC.FLX, sappear.flx, SFXTMPL.FLX and Options.ini are the originals again."
    return
}

if (Test-Path $backup) { throw "$backup exists: the arena is already in (run -Uninstall first)." }
foreach ($rel in $added) {
    if (-not (Test-Path (Join-Path $stage $rel))) { throw "$stage has no $rel." }
}
# the data files a test changes are optional: those it does not have stay the game's own
$dataFiles = @("runtime\NPC.FLX", "static\sappear.flx", "sound\SFXTMPL.FLX") | Where-Object { Test-Path (Join-Path $stage $_) }
foreach ($rel in $added) { if (Test-Path (Find-File $rel)) { throw "$(Find-File $rel) already exists; not overwriting it." } }

New-Item -ItemType Directory -Force (Join-Path $backup "runtime"), (Join-Path $backup "static"), (Join-Path $backup "sound") | Out-Null
foreach ($rel in $replaced) { Copy-Item (Find-File $rel) (Join-Path $backup $rel) -Force }
$sg = Join-Path $Game "savegame"
$names = @(); if (Test-Path $sg) { $names = @(Get-ChildItem -LiteralPath $sg -File | ForEach-Object { $_.Name }) }
[IO.File]::WriteAllLines((Join-Path $backup "savegame-before.txt"), [string[]]$names)

foreach ($rel in $dataFiles) { Copy-Item (Join-Path $stage $rel) (Find-File $rel) -Force }
foreach ($rel in $added) { Copy-Item (Join-Path $stage $rel) (Join-Path $Game $rel) -Force }

# [Game] Start=<absolute path of arena.ini>: the game's developer start reads it as the new game's start
$opt = Find-File "Options.ini"
$text = [IO.File]::ReadAllText($opt, $latin1)
$ini = Join-Path $Game "arena.ini"
$lines = $text -split "`r?`n"
$inGame = $false; $done = $false
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match '^\s*\[(.+)\]') { $inGame = ($Matches[1] -ieq "Game") }
    elseif ($inGame -and $lines[$i] -match '^\s*Start\s*=') { $lines[$i] = "Start=$ini"; $done = $true }
}
if (-not $done) { throw "Options.ini has no Start= line in [Game]; nothing changed there (run -Uninstall)." }
[IO.File]::WriteAllText($opt, ($lines -join "`r`n"), $latin1)

"Arena in: a new game starts on map 91 (Options.ini [Game] Start=$ini). -Uninstall puts everything back."
if ($Run) {
    $p = Start-Process -FilePath (Join-Path $Game "u9.exe") -WorkingDirectory $Game -PassThru
    "u9.exe started (pid $($p.Id)); closing it after $Minutes minutes."
    if (-not $p.WaitForExit([int]($Minutes * 60000))) {
        "Time is up: closing the game."
        # u9.exe may have been relaunched by U9GL's launcher: close every u9 process from this folder
        $procs = @(Get-Process -Name u9, u9-mod -ErrorAction SilentlyContinue)
        foreach ($q in $procs) { [void]$q.CloseMainWindow() }
        Start-Sleep -Seconds 5
        foreach ($q in $procs) { if (-not $q.HasExited) { Stop-Process -Id $q.Id -Force } }
    }
    # a launcher may have handed over to another u9 process: wait for every one to go
    while (Get-Process -Name u9, u9-mod -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
    "The game has exited."
    if (-not $Keep) { & $PSCommandPath -Game $Game -Uninstall }
}
