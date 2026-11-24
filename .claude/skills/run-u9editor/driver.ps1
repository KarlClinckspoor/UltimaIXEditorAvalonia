# Drives the U9 editor's window on the Windows desktop. Run from the repo root:
#   powershell -ExecutionPolicy Bypass -File .claude/skills/run-u9editor/driver.ps1 <command> [args]
#
#   launch [editor args...]    build-free start of src/U9Editor.App/bin/Debug/net10.0/U9Editor.exe,
#                              waits until the window is up and the map is drawn
#   shot <png> [x y w h]       capture the window (or a window-relative rectangle of it)
#   move <x> <y>               hover (the status bar then names the point and any object under it)
#   click <x> <y>              left click, window-relative pixels (as in a full `shot`)
#   drag <x1> <y1> <x2> <y2>   left drag in 20 steps
#   key <keys>                 SendKeys string: {PGUP} ^z ^s {ESC} {END} {DEL} [ ] ...
#   stop                       close the editor (kills it: no unsaved-changes prompt)
param([Parameter(Mandatory = $true)][string]$cmd, [Parameter(ValueFromRemainingArguments = $true)][string[]]$rest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
if (-not ('U9Win' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U9Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  public struct RECT { public int L, T, R, B; }
}
"@
}
# physical pixels everywhere, whatever the display scaling
[U9Win]::SetProcessDPIAware() | Out-Null

function Get-EditorWindow {
    $p = Get-Process U9Editor -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "No U9Editor window: run 'launch' first" }
    return $p.MainWindowHandle
}

function Select-EditorWindow {
    $h = Get-EditorWindow
    [U9Win]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 200
    $r = New-Object U9Win+RECT
    [U9Win]::GetWindowRect($h, [ref]$r) | Out-Null
    return $r
}

function Move-To($r, $x, $y) { [U9Win]::SetCursorPos($r.L + [int]$x, $r.T + [int]$y) | Out-Null; Start-Sleep -Milliseconds 60 }

switch ($cmd) {
    'launch' {
        $exe = 'src\U9Editor.App\bin\Debug\net10.0\U9Editor.exe'
        if (-not (Test-Path $exe)) { throw "$exe not found: dotnet build src/U9Editor.App first" }
        $args2 = if ($rest) { $rest -join ' ' } else { '--gamedata gamedata --map 9 --size 1400x900' }
        Start-Process -FilePath $exe -ArgumentList $args2 | Out-Null
        # the window appears in ~2 s; Britannia's models take a few more to build
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Milliseconds 500
            $p = Get-Process U9Editor -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
            if ($p) { break }
        }
        if (-not $p) { throw "U9Editor did not open a window in 30 s" }
        # Windows cascades new windows down and right until the status bar is under the taskbar: park it at 0,0
        # (SWP_NOSIZE | SWP_NOZORDER)
        [U9Win]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0004) | Out-Null
        Start-Sleep -Seconds 10
        "launched pid $($p.Id)"
    }
    'shot' {
        $r = Select-EditorWindow
        $w = $r.R - $r.L; $h = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
        if ($rest.Count -ge 5) {
            # New-Object with bracketed comma arguments builds a wrong rectangle, and Clone then says 'Out of memory'
            $rect = [System.Drawing.Rectangle]::new([int]$rest[1], [int]$rest[2], [int]$rest[3], [int]$rest[4])
            $crop = $bmp.Clone($rect, $bmp.PixelFormat)
            $crop.Save($rest[0])
        }
        else { $bmp.Save($rest[0]) }
        "window $w x $h at $($r.L),$($r.T) -> $($rest[0])"
    }
    'move' {
        $r = Select-EditorWindow
        Move-To $r $rest[0] $rest[1]
        Move-To $r ([int]$rest[0] + 1) $rest[1]
    }
    'click' {
        $r = Select-EditorWindow
        Move-To $r $rest[0] $rest[1]
        [U9Win]::mouse_event(2, 0, 0, 0, 0); Start-Sleep -Milliseconds 50; [U9Win]::mouse_event(4, 0, 0, 0, 0)
    }
    'drag' {
        $r = Select-EditorWindow
        $x1 = [int]$rest[0]; $y1 = [int]$rest[1]; $x2 = [int]$rest[2]; $y2 = [int]$rest[3]
        Move-To $r $x1 $y1
        [U9Win]::mouse_event(2, 0, 0, 0, 0); Start-Sleep -Milliseconds 100
        for ($i = 1; $i -le 20; $i++) { Move-To $r ($x1 + ($x2 - $x1) * $i / 20) ($y1 + ($y2 - $y1) * $i / 20) }
        Start-Sleep -Milliseconds 100; [U9Win]::mouse_event(4, 0, 0, 0, 0)
    }
    'key' {
        Select-EditorWindow | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait($rest[0])
    }
    'stop' {
        Get-Process U9Editor -ErrorAction SilentlyContinue | Stop-Process -Force
        "stopped"
    }
    default { throw "unknown command $cmd" }
}
