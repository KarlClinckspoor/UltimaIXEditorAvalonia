# Keeps Windows from blanking the screen, sleeping or locking for idleness while in-game tests run (the game and
# the screenshots need an unlocked desktop). Holds until stopped (Ctrl+C or the process is killed), or for -Minutes.
#
#   powershell -ExecutionPolicy Bypass -File tools/keep-awake.ps1               # until stopped
#   powershell -ExecutionPolicy Bypass -File tools/keep-awake.ps1 -Minutes 90
#
# SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) lasts while this thread lives, so
# nothing is left changed afterwards. A group policy "machine inactivity limit" can still lock regardless.
param([int]$Minutes = 0)
Add-Type -Namespace U9 -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
$flags = [uint32]"0x80000003"
if ([U9.Power]::SetThreadExecutionState($flags) -eq 0) { throw "SetThreadExecutionState failed" }
$until = if ($Minutes -gt 0) { (Get-Date).AddMinutes($Minutes) } else { [DateTime]::MaxValue }
"Keeping the PC awake$(if ($Minutes -gt 0) { " until $($until.ToString('HH:mm'))" } else { ' until stopped' })."
try {
    while ((Get-Date) -lt $until) {
        Start-Sleep -Seconds 30
        [void][U9.Power]::SetThreadExecutionState($flags)   # refresh; harmless
    }
} finally {
    [void][U9.Power]::SetThreadExecutionState([uint32]"0x80000000")
    "Back to normal power settings."
}
