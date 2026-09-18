#requires -Version 5.1
<#
.SYNOPSIS
Proves that a program moving the seat's cursor cannot move the real pointer.

.DESCRIPTION
Runs a probe inside an already running seat that calls SetCursorPos the way an SDL game does,
while a watcher on this desktop samples the real pointer. Passes when the daemon's pointer guard
suppressed the resulting Remote Desktop pointer updates and the real pointer never jumped.

The Remote Desktop control only tries to move the real pointer while it is over the viewer's
rectangle, whether or not the viewer is shown. Each phase therefore waits until your pointer is
inside that rectangle (it is printed) and you should then leave the mouse alone for a few seconds.
-PlacePointer moves the pointer there for you instead; nothing else in this test moves it.

Never starts or stops a seat. The default run leaves the viewer exactly as it is and only tests a
view-only viewer. It does move the SEAT's cursor, so hold the desktop lease and do not run it while
a game in the seat is mid-play.

-IncludeVisible also tests the opposite visibility. Showing the viewer activates it on this desktop.
-IncludeControl also takes control with the viewer focused, where seat pointer moves are expected to
reach the real pointer, as in any Remote Desktop client. It moves your pointer on purpose.
#>
param(
    [string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\pointer-test'),
    [ValidateRange(2, 40)][int]$Moves = 6,
    [ValidateRange(50, 5000)][int]$IntervalMs = 400,
    [ValidateRange(0, 600)][int]$WaitSeconds = 60,
    [switch]$PlacePointer,
    [switch]$IncludeVisible,
    [switch]$IncludeControl
)
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
function Call-Anode([string[]]$Arguments) {
    $resultText = (& $Anode @Arguments | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Anode command failed: $($Arguments[0])" }
    if ($resultText.Trim() -eq 'ok') { return $null }
    return $resultText | ConvertFrom-Json
}
function Assert-That($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Wait-File([string]$Path, [int]$TimeoutMs, $Process = $null) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while (-not (Test-Path -LiteralPath $Path) -and (Get-Date) -lt $deadline -and -not ($Process -and $Process.HasExited)) { Start-Sleep -Milliseconds 100 }
    return Test-Path -LiteralPath $Path
}
function Read-Utc($Value) { return ([datetime]$Value).ToUniversalTime() }
function Set-Viewer([bool]$Visible, [bool]$ViewOnly) {
    $null = Call-Anode @($(if ($Visible) { 'show' } else { 'hide' }))
    $null = Call-Anode @('control', $(if ($ViewOnly) { 'give' } else { 'take' }))
    Start-Sleep -Milliseconds 700
    $now = Call-Anode @('status', '--json')
    Assert-That ($now.viewerVisible -eq $Visible -and $now.viewOnly -eq $ViewOnly) 'The viewer did not reach the requested state.'
}
function Test-Phase([string]$Name, [bool]$ExpectForwarding) {
    $probeReport = Join-Path $OutputDirectory "$Name-probe.json"
    $watchReport = Join-Path $OutputDirectory "$Name-watch.json"
    Remove-Item -LiteralPath $probeReport, $watchReport, "$watchReport.ready" -ErrorAction SilentlyContinue
    $before = (Call-Anode @('status', '--json')).pointerGuard
    $area = $before.viewer
    Assert-That ($null -ne $area) 'The daemon did not report where its viewer is.'
    $duration = $Moves * $IntervalMs + 4000
    $watchArguments = @('__pointer-watch', '--report', "`"$watchReport`"", '--duration', "$duration", '--wait', "$($WaitSeconds * 1000)",
        '--rect', "$($area.x),$($area.y),$($area.width),$($area.height)")
    if ($PlacePointer) { $watchArguments += '--place' }
    else { Write-Host "$Name`: move your pointer well inside x $($area.x)..$($area.x + $area.width), y $($area.y)..$($area.y + $area.height), then leave the mouse alone." }
    $watcher = Start-Process -FilePath $Anode -ArgumentList $watchArguments -PassThru -WindowStyle Hidden
    Assert-That (Wait-File "$watchReport.ready" ($WaitSeconds * 1000 + 5000) $watcher) "$Name`: inconclusive. The real pointer never entered the viewer's rectangle, where the Remote Desktop control would try to move it. Use -PlacePointer."
    Start-Sleep -Milliseconds 500
    $launched = Call-Anode @('run', $Anode, '__cursor-probe', '--report', $probeReport, '--moves', "$Moves", '--interval', "$IntervalMs")
    Assert-That ($launched.session -eq $status.session) 'The probe launched in the wrong session.'
    Assert-That (Wait-File $probeReport ($Moves * $IntervalMs + 20000)) 'The seat probe did not report.'
    Assert-That $watcher.WaitForExit($duration + 15000) 'The pointer watcher did not finish.'
    $probe = Get-Content -LiteralPath $probeReport -Raw | ConvertFrom-Json
    $watch = Get-Content -LiteralPath $watchReport -Raw | ConvertFrom-Json
    $after = (Call-Anode @('status', '--json')).pointerGuard

    Assert-That ($probe.session -eq $status.session -and $watch.session -eq $status.parentSession) 'A report came from the wrong session.'
    Assert-That (@($probe.moves | Where-Object { -not $_.ok }).Count -eq 0) 'SetCursorPos failed inside the seat, so nothing was tested.'
    Assert-That ($watch.samples -gt 500) 'The watcher could not read the real pointer often enough (secure desktop or lock screen?).'
    # A leak lands inside the viewer within milliseconds of a seat move. A hand moving the mouse does neither reliably.
    $leaks = @($watch.jumps | Where-Object {
        $jump = $_; $at = Read-Utc $jump.utc
        $landedInside = $jump.toX -ge $area.x -and $jump.toX -lt ($area.x + $area.width) -and $jump.toY -ge $area.y -and $jump.toY -lt ($area.y + $area.height)
        $landedInside -and @($probe.moves | Where-Object { $moved = Read-Utc $_.utc; $at -ge $moved.AddMilliseconds(-20) -and $at -le $moved.AddMilliseconds(250) }).Count -gt 0
    })
    $suppressed = $after.suppressed - $before.suppressed
    $forwarded = $after.forwarded - $before.forwarded
    $result = [pscustomobject]@{ phase=$Name; seatMoves=@($probe.moves).Count; suppressed=$suppressed; forwarded=$forwarded; realPointerJumps=$leaks.Count; samples=$watch.samples; pointerPlaced=$watch.placed; note=$null }
    if ($ExpectForwarding) {
        # Windows may refuse to give a background daemon the foreground; then the gate rightly stays shut.
        if ($forwarded -eq 0) { $result.note = 'inconclusive: the viewer was not the foreground window, so nothing was forwarded' }
        return $result
    }
    Assert-That ($leaks.Count -eq 0) "$Name`: the real pointer jumped $($leaks.Count) time(s) as the seat moved its cursor: $($leaks | ConvertTo-Json -Compress)"
    Assert-That ($forwarded -eq 0) "$Name`: the guard forwarded $forwarded pointer move(s) to the real desktop."
    Assert-That ($suppressed -ge 1) "$Name`: inconclusive. The seat moved its cursor but the Remote Desktop control attempted no pointer move (did the pointer leave the viewer's rectangle?)."
    return $result
}

$status = Call-Anode @('status', '--json')
Assert-That ($status.state -eq 'ready' -and $status.session -ne $status.parentSession) 'An existing verified seat is required.'
Assert-That ($null -ne $status.pointerGuard) 'This daemon predates the pointer guard. It has to be restarted from this build, which interrupts the seat; arrange that first.'
Assert-That $status.pointerGuard.installed 'The daemon could not patch the Remote Desktop control; see its log.'
Assert-That $status.viewOnly 'You have control of the seat. Release control first; this test will not take it from you.'
# The caller supplies an existing lease; never acquire/start a seat from this opt-in test.
$null = Call-Anode @('lease', 'renew', '--ttl', '600')

$results = @()
try {
    $results += Test-Phase $(if ($status.viewerVisible) { 'visible-view-only' } else { 'hidden-view-only' }) $false
    if ($IncludeVisible) {
        Set-Viewer (-not $status.viewerVisible) $true
        $results += Test-Phase $(if ($status.viewerVisible) { 'hidden-view-only' } else { 'visible-view-only' }) $false
    }
    if ($IncludeControl) {
        Set-Viewer $true $false
        $results += Test-Phase 'focused-with-control' $true
    }
} finally {
    if ($IncludeVisible -or $IncludeControl) { Set-Viewer $status.viewerVisible $true }
}
$final = Call-Anode @('status', '--json')
Assert-That ($final.session -eq $status.session -and $final.viewerVisible -eq $status.viewerVisible -and $final.viewOnly) 'Seat or parent viewer state changed.'
[pscustomobject]@{ passed=$true; session=$status.session; phases=$results } |
    ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.json')
