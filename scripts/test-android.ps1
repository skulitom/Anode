#requires -Version 5.1
<# Opt-in test of an Android emulator in an existing ready seat. Requires the caller's desktop lease
   (ANODE_AGENT_ID and ANODE_LEASE_TOKEN). It starts an emulator, which loads the machine, so run it only
   while the seat is free. Never starts or stops a seat or opens its viewer; it stops the emulator it
   started even when a step fails.
#>
param([Parameter(Mandatory = $true)][string]$Avd,
    [ValidateSet('auto', 'host', 'software')][string]$Gpu = 'auto',
    [string]$Anode,
    [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# Windows PowerShell leaves $PSScriptRoot empty in the defaults of a script with a mandatory parameter.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Anode) { $Anode = Join-Path $here '..\dist\anode.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $here '..\artifacts\android-test' }
$Anode = (Resolve-Path -LiteralPath $Anode).Path
function Invoke-AnodeJson([string[]]$Arguments) {
    $text = & $Anode @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "anode $($Arguments -join ' ') failed: $text" }
    $text | ConvertFrom-Json
}
function Invoke-Adb([string]$Serial, [string[]]$Arguments) {
    $text = & $Anode android adb $Serial -- @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "adb $($Arguments -join ' ') failed with $($LASTEXITCODE): $text" }
    $text.Trim()
}

$before = Invoke-AnodeJson @('status', '--json')
if ($before.state -ne 'ready' -or $before.session -eq $before.parentSession) { throw 'An existing ready child session is required.' }
# Renewal cannot start a daemon or seat, or take another agent's desktop.
$null = Invoke-AnodeJson @('lease', 'renew', '--ttl', '600')
$android = Invoke-AnodeJson @('android', '--json')
if (-not $android.sdk) { throw 'No Android SDK was found.' }
if (-not ($android.avds | Where-Object { $_.name -eq $Avd })) { throw "No AVD is named $Avd. Available: $(@($android.avds | ForEach-Object name) -join ', ')" }
if ($android.emulators | Where-Object { $_.inSeat }) { throw 'An emulator is already running in the seat. Stop it first.' }
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory -Force

$serial = $null
try {
    $started = Invoke-AnodeJson @('android', 'start', $Avd, '--gpu', $Gpu, '--wait', '150', '--json')
    $serial = $started.serial
    if (-not $started.booted) { throw "$serial had not finished booting after $($started.waitedSeconds) s. Its output: $($started.log)" }
    if ((Invoke-Adb $serial @('shell', 'getprop', 'sys.boot_completed')) -ne '1') { throw "adb does not report $serial booted." }
    $physical = [regex]::Match((Invoke-Adb $serial @('shell', 'wm', 'size')), '(\d+)x(\d+)')
    $picture = Join-Path $runDirectory "$serial.png"
    $saved = & $Anode android shot $serial $picture | Out-String
    if ($LASTEXITCODE -ne 0) { throw "The screenshot failed: $saved" }
    $captured = [regex]::Match($saved, 'of (\d+)x(\d+)')
    if (-not $captured.Success -or ($physical.Success -and $captured.Value -ne "of $($physical.Value)")) {
        throw "The screenshot is $($captured.Value), but the device reports $($physical.Value). See $picture"
    }
    $listed = (Invoke-AnodeJson @('android', '--json')).emulators | Where-Object { $_.serial -eq $serial }
    if (-not $listed.inSeat) { throw "$serial is not reported as running in the seat." }
    $stopped = Invoke-AnodeJson @('android', 'stop', $serial, '--json')
    $serial = $null
    Start-Sleep -Seconds 2
    if ((Invoke-AnodeJson @('android', '--json')).emulators | Where-Object { $_.serial -eq $started.serial }) { throw "$($started.serial) is still running after stop." }
    $after = Invoke-AnodeJson @('status', '--json')
    if ($after.session -ne $before.session -or $after.viewerVisible -ne $before.viewerVisible) { throw 'Session or viewer state changed.' }
    [pscustomobject]@{ passed = $true; session = $before.session; avd = $Avd; serial = $started.serial; gpu = $Gpu
        bootSeconds = $started.waitedSeconds; screen = $physical.Value; screenshot = $picture; stopped = $stopped.method; viewerUnchanged = $true
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $runDirectory 'result.json')
} finally {
    if ($serial) { & $Anode android stop $serial | Out-Null }
}
