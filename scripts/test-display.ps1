#requires -Version 5.1
<# Opt-in test of display changes in an existing ready seat. Requires the caller's desktop lease
   (ANODE_AGENT_ID and ANODE_LEASE_TOKEN). Apps in the seat see every change, so run it only while the
   seat is free for testing. Never starts or stops a seat or opens its viewer; it restores the startup
   display even when a step fails.
#>
param([string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\display-test'))
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
function Invoke-AnodeJson([string[]]$Arguments) {
    $text = & $Anode @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "anode $($Arguments -join ' ') failed: $text" }
    $text | ConvertFrom-Json
}
function Get-Label($Display) { "$($Display.width)x$($Display.height) at $($Display.scale)%" }

$before = Invoke-AnodeJson @('status', '--json')
if ($before.state -ne 'ready' -or $before.session -eq $before.parentSession) { throw 'An existing ready child session is required.' }
# Renewal cannot start a daemon or seat, or take another agent's desktop.
$null = Invoke-AnodeJson @('lease', 'renew', '--ttl', '300')
$startup = $before.startupDisplay
if (-not $startup -or -not $before.seat.screen.scale) { throw 'This daemon does not report displays; run the new build.' }
if ((Get-Label $before.seat.screen) -ne (Get-Label $startup)) { throw "The seat is at $(Get-Label $before.seat.screen), not its startup display; reset it first." }
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory -Force

$displays = @(
    @{ width = 1024; height = 768; scale = 100 }, @{ width = 1920; height = 1080; scale = 150 },
    @{ width = 1080; height = 1920; scale = 100 }, @{ width = 2560; height = 1440; scale = 125 })
$results = [Collections.Generic.List[object]]::new()
$changed = $false
try {
    foreach ($display in $displays) {
        $label = Get-Label $display
        $picture = Join-Path $runDirectory ("{0}x{1}-{2}.png" -f $display.width, $display.height, $display.scale)
        $changed = $true
        $result = Invoke-AnodeJson @('display', "$($display.width)x$($display.height)", '--scale', "$($display.scale)", '--shot', $picture, '--json')
        if ((Get-Label $result) -ne $label) { throw "Asked for $label, got $(Get-Label $result)." }
        if ($result.screenshot.sourceWidth -ne $display.width -or $result.screenshot.sourceHeight -ne $display.height) {
            throw "The screenshot at $label was $($result.screenshot.sourceWidth)x$($result.screenshot.sourceHeight). See $picture"
        }
        $status = Invoke-AnodeJson @('status', '--json')
        if ((Get-Label $status.seat.screen) -ne $label) { throw "status reports $(Get-Label $status.seat.screen) after $label." }
        $results.Add([pscustomobject]@{ display = $label; method = $result.method; elapsedMs = $result.elapsedMs; screenshot = $picture })
    }
    $reset = Invoke-AnodeJson @('display', 'reset', '--json')
    $changed = $false
    if ((Get-Label $reset) -ne (Get-Label $startup)) { throw "reset left $(Get-Label $reset), not $(Get-Label $startup)." }
    $after = Invoke-AnodeJson @('status', '--json')
    if ($after.session -ne $before.session -or $after.viewerVisible -ne $before.viewerVisible -or (Get-Label $after.seat.screen) -ne (Get-Label $startup)) {
        throw 'The session, the viewer or the restored display changed.'
    }
    [pscustomobject]@{ passed = $true; session = $before.session; startup = Get-Label $startup; displays = $results; viewerUnchanged = $true
    } | ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $runDirectory 'result.json')
} finally {
    if ($changed) { & $Anode display reset --json | Out-Null }
}
