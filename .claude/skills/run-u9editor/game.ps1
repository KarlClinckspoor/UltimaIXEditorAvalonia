# Drives u9.exe (with tools/game-test.ps1's U9GL in) for in-game tests.
#   game.ps1 front                      bring the game (and U9GL's window) to the front
#   game.ps1 tap <key> [<key> ...]      press keys one after another: esc, enter, space, up, down, left, right,
#                                       a..z, 0..9, f1..f12, or ctrl+<key>
#   game.ps1 hold <key> <ms>            hold a key down (walking: hold up 1500)
#   game.ps1 shot                       Ctrl+P in the game; moves the new u9-<n>-after.png to %TEMP%\u9game-<n>.png
#   game.ps1 log [<n>]                  the last n lines (default 15) of U9GL's log, %TEMP%\u9log.txt
#   game.ps1 quit                       Ctrl+X, Enter
# The game reads keys as scan codes (keybd_event with MapVirtualKey); arrows need the extended flag. It draws
# its own cursor, so mouse clicks miss.
param([string]$cmd, [Parameter(ValueFromRemainingArguments = $true)][string[]]$rest)
if (-not ('U9Keys' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U9Keys {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
  static uint Ext(byte v) { return (v >= 0x21 && v <= 0x28) || v == 0x2D || v == 0x2E ? 1u : 0u; }
  public static void Down(byte v) { keybd_event(v, (byte)MapVirtualKey(v, 0), Ext(v), IntPtr.Zero); }
  public static void Up(byte v) { keybd_event(v, (byte)MapVirtualKey(v, 0), Ext(v) | 2, IntPtr.Zero); }
}
"@
}
$game = Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot))) "gamedata"

function Front {
    $p = Get-Process u9 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "u9.exe is not running" }
    foreach ($w in Get-Process u9 | ForEach-Object { $_.MainWindowHandle } | Where-Object { $_ -ne 0 }) { [U9Keys]::ShowWindow($w, 9) | Out-Null; [U9Keys]::SetForegroundWindow($w) | Out-Null }
    Start-Sleep -Milliseconds 300
}

function Vk([string]$k) {
    $named = @{ esc = 0x1B; enter = 0x0D; space = 0x20; tab = 0x09; up = 0x26; down = 0x28; left = 0x25; right = 0x27; ctrl = 0x11; shift = 0x10; alt = 0x12; backquote = 0xC0 }
    if ($named.ContainsKey($k)) { return [byte]$named[$k] }
    if ($k -match '^f(\d+)$') { return [byte](0x6F + [int]$Matches[1]) }
    if ($k.Length -eq 1) { return [byte][char]$k.ToUpper() }
    throw "unknown key '$k'"
}

function Tap([string]$k, [int]$ms = 80) {
    $parts = $k.ToLower() -split '\+'
    $mods = @($parts | Select-Object -SkipLast 1 | ForEach-Object { Vk $_ })
    $v = Vk $parts[-1]
    foreach ($m in $mods) { [U9Keys]::Down($m); Start-Sleep -Milliseconds 50 }
    [U9Keys]::Down($v); Start-Sleep -Milliseconds $ms; [U9Keys]::Up($v)
    foreach ($m in $mods) { Start-Sleep -Milliseconds 50; [U9Keys]::Up($m) }
    Start-Sleep -Milliseconds 250
}

switch ($cmd) {
    'front' { Front }
    'tap' { Front; foreach ($k in $rest) { Tap $k } }
    'hold' { Front; Tap $rest[0] ([int]$rest[1]) }
    'shot' {
        Front
        $before = @(Get-ChildItem $game -Filter "u9-*-after.png" | ForEach-Object Name)
        Tap "ctrl+p"
        for ($i = 0; $i -lt 20; $i++) {
            $new = Get-ChildItem $game -Filter "u9-*-after.png" | Where-Object { $before -notcontains $_.Name } | Select-Object -First 1
            if ($new) { break }; Start-Sleep -Milliseconds 250
        }
        if (-not $new) { throw "no screenshot appeared (is gl_screenshot = 2 set, and a world showing?)" }
        Start-Sleep -Milliseconds 300
        $to = Join-Path $env:TEMP ("u9game-" + ($new.BaseName -replace '\D', '') + ".png")
        Move-Item $new.FullName $to -Force; $to
    }
    'log' { $n = if ($rest) { [int]$rest[0] } else { 15 }; Get-Content "$env:TEMP\u9log.txt" -Tail $n }
    'quit' { Front; Tap "ctrl+x"; Start-Sleep -Milliseconds 800; Tap "enter" }
    default { throw "usage: game.ps1 front | tap <keys> | hold <key> <ms> | shot | log [n] | quit" }
}
