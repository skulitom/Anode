#requires -Version 5.1
<#
.SYNOPSIS
Tests set_value and observation on a Chrome form in an already running Anode seat.
Uses a disposable local page and a temporary Chrome profile, never the seat's signed-in profile.
Never starts a seat or activates the parent viewer; it renews the lease you already hold.
.DESCRIPTION
Checks that set_value on a text field still succeeds and reads back within its 1.5-second wait, that
set_value on a drop-down list (<select>) either changes the choice or fails instead of reporting
success, and that an observation taken while the list is open lists each control once. It also reports
whether the open list's options appear in the tree, and chooses one when they do.
#>
param(
    [string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'),
    [string]$Chrome,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\chrome-form-test')
)
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
if (-not $Chrome) {
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Google\Chrome\Application\chrome.exe'
        if (Test-Path -LiteralPath $candidate) { $Chrome = $candidate; break }
    }
}
if (-not $Chrome) { throw 'Chrome was not found. Pass -Chrome with the path to chrome.exe.' }
$Chrome = (Resolve-Path -LiteralPath $Chrome).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$title = "Anode form check $stamp"
$page = Join-Path $OutputDirectory 'form.html'
$profileDirectory = Join-Path $OutputDirectory "profile-$stamp"
Set-Content -LiteralPath $page -Encoding UTF8 -Value @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>$title</title></head>
<body>
<p><label for="name">Name</label> <input id="name" type="text"></p>
<p><label for="plan">Plan</label> <select id="plan"><option>Free</option><option>Team</option><option>Enterprise</option></select></p>
<p><input id="result" aria-label="Result" readonly value="Plan: Free"></p>
<script>
document.getElementById('plan').addEventListener('change', function (event) {
  document.getElementById('result').value = 'Plan: ' + event.target.value;
});
</script>
</body></html>
"@

function Call-Anode([string[]]$Arguments) {
    if ($Arguments[0] -in @('windows','inspect','window','element','wait') -and $Arguments -notcontains '--json') { $Arguments += '--json' }
    $resultText = (& $Anode @Arguments | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Anode command failed: $($Arguments[0])" }
    return $resultText | ConvertFrom-Json
}
# For a command that may fail on purpose: its exit code and everything it wrote.
function Try-Anode([string[]]$Arguments) {
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $text = (& $Anode @Arguments 2>&1 | Out-String)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
    } finally { $ErrorActionPreference = $savedPreference }
}
function Assert-That($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Find-Control($Observation, [string]$AutomationId) { @($Observation.elements | Where-Object { $_.automationId -eq $AutomationId }) }

$status = Call-Anode @('status', '--json')
Assert-That ($status.state -eq 'ready' -and $status.session -ne $status.parentSession) 'An existing verified seat is required.'
# The caller supplies an existing lease; never acquire/start a seat from this opt-in test.
$null = Call-Anode @('lease', 'renew', '--ttl', '600')
$url = ([Uri]$page).AbsoluteUri
$windowId = $null
# The Chrome processes this test started, by id and start time, so cleanup never touches another one.
$owned = @()
function Add-Owned([int]$ProcessId) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($process -and $process.SessionId -eq $status.session -and -not ($script:owned | Where-Object { $_.Id -eq $ProcessId })) {
        $script:owned += [pscustomobject]@{ Id = $ProcessId; Started = $process.StartTime }
    }
}
$warnings = @()
try {
    $launched = Call-Anode @('run', $Chrome, "--user-data-dir=$profileDirectory", '--no-first-run', '--no-default-browser-check',
        '--force-renderer-accessibility', "--app=$url")
    Assert-That ($launched.session -eq $status.session) 'Chrome launched in the wrong session.'
    Add-Owned ([int]$launched.pid)
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $windows = @((Call-Anode @('windows', '--query', $title)).windows)
        if ($windows.Count -eq 0) { Start-Sleep -Milliseconds 250 }
    } while ($windows.Count -eq 0 -and (Get-Date) -lt $deadline)
    Assert-That ($windows.Count -eq 1) "Chrome's window '$title' did not appear exactly once."
    $windowId = $windows[0].windowId
    $chrome = Get-Process -Id ([int]$windows[0].pid)
    Assert-That ($chrome.SessionId -eq $status.session) 'Chrome window is outside the seat.'
    Add-Owned $chrome.Id
    # Page controls sit deep in Chrome's tree, and Chrome builds the tree only once something asks.
    function Observe-Page { Call-Anode @('inspect', $windowId, '--max-depth', '16', '--max-elements', '500') }
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $observation = Observe-Page
        $ready = @(Find-Control $observation 'name').Count -gt 0 -and @(Find-Control $observation 'plan').Count -gt 0
        if (-not $ready) { Start-Sleep -Milliseconds 500 }
    } while (-not $ready -and (Get-Date) -lt $deadline)
    Assert-That $ready "The page's fields never appeared in Chrome's accessibility tree."

    # A text field: set_value succeeds only once the field reads the new value back.
    $field = @(Find-Control $observation 'name')[0]
    Assert-That ($field.actions -contains 'set_value') 'The text field offers no set_value.'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Call-Anode @('element', $observation.snapshotId, $field.id, 'set_value', '--value', 'Sam Example')
    $watch.Stop()
    $fieldMilliseconds = $watch.ElapsedMilliseconds
    $observation = Observe-Page
    $field = @(Find-Control $observation 'name')[0]
    Assert-That ($field.text -eq 'Sam Example') "The text field reads '$($field.text)' after set_value."

    # A drop-down list: Chrome may ignore set_value, which must then fail instead of reporting success.
    $plan = @(Find-Control $observation 'plan')[0]
    Assert-That ($plan.text -eq 'Free') "The drop-down list starts at '$($plan.text)', not Free."
    $attempt = Try-Anode @('element', $observation.snapshotId, $plan.id, 'set_value', '--value', 'Team')
    $observation = Observe-Page
    $plan = @(Find-Control $observation 'plan')[0]
    $result = @(Find-Control $observation 'result')[0]
    if ($attempt.ExitCode -eq 0) {
        Assert-That ($plan.text -eq 'Team' -and $result.text -eq 'Plan: Team') 'set_value on the drop-down list reported success, but the choice did not change.'
        $dropDown = 'applied'
    } else {
        Assert-That ($attempt.Text -match 'still showed its old value') "set_value on the drop-down list failed for another reason: $($attempt.Text.Trim())"
        Assert-That ($plan.text -eq 'Free' -and $result.text -eq 'Plan: Free') 'set_value on the drop-down list failed, but the choice changed.'
        $dropDown = 'refused'
    }

    # The open list: each control once, and the options if Chrome lists them.
    Assert-That ($plan.actions -contains 'expand') 'The drop-down list offers no expand.'
    $null = Call-Anode @('element', $observation.snapshotId, $plan.id, 'expand')
    Start-Sleep -Milliseconds 500
    $open = Observe-Page
    $copies = @(Find-Control $open 'name').Count
    Assert-That ($copies -eq 1) "With the list open, the observation listed the text field $copies times."
    $skippedRepeats = 0
    if ($open.PSObject.Properties['skippedRepeats']) { $skippedRepeats = [int]$open.skippedRepeats }
    $target = if ($dropDown -eq 'applied') { 'Enterprise' } else { 'Team' }
    $option = @($open.elements | Where-Object { $_.role -eq 'ListItem' -and $_.name -eq $target }) | Select-Object -First 1
    $optionChosen = $false
    if (-not $option) {
        $warnings += "The open list's options were not in the observation; choose an option with seat_screenshot and seat_click."
    } else {
        $action = @('select', 'invoke') | Where-Object { $option.actions -contains $_ } | Select-Object -First 1
        if (-not $action) {
            $warnings += "The option '$target' was listed without select or invoke."
        } else {
            $null = Call-Anode @('element', $open.snapshotId, $option.id, $action)
            $chosen = Try-Anode @('wait', $windowId, '--automation-id', 'result', '--text', "Plan: $target", '--wait', '5000', '--json')
            $optionChosen = $chosen.ExitCode -eq 0
            if (-not $optionChosen) { $warnings += "'$action' on the option '$target' did not change the choice within 5 seconds." }
        }
    }
    $finalStatus = Call-Anode @('status', '--json')
    Assert-That ($finalStatus.session -eq $status.session -and $finalStatus.viewerVisible -eq $status.viewerVisible) 'Seat or parent viewer state changed.'
    foreach ($warning in $warnings) { Write-Warning $warning }
    [pscustomobject]@{
        passed = $true; session = $status.session; chrome = $Chrome; chromeVersion = (Get-Item -LiteralPath $Chrome).VersionInfo.ProductVersion
        textFieldSetValueMs = $fieldMilliseconds; dropDownSetValue = $dropDown; openListSkippedRepeats = $skippedRepeats
        optionsListed = [bool]$option; optionChosen = $optionChosen; warnings = $warnings
        checks = @('page in the accessibility tree', 'text field set_value read back', 'drop-down set_value changes the choice or fails',
            'open list observed without repeats', 'parent viewer unchanged')
    } | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($windowId) { & $Anode window $windowId close | Out-Host }
    foreach ($item in $owned) {
        $remaining = Get-Process -Id $item.Id -ErrorAction SilentlyContinue
        if ($remaining -and $remaining.SessionId -eq $status.session -and $remaining.StartTime -eq $item.Started) {
            if (-not $remaining.WaitForExit(5000)) { & $Anode ps kill $item.Id | Out-Host }
        }
    }
    # Chrome's helper processes can hold the profile for a moment after the browser exits.
    for ($try = 0; $try -lt 10 -and (Test-Path -LiteralPath $profileDirectory); $try++) {
        try { Remove-Item -LiteralPath $profileDirectory -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
    if (Test-Path -LiteralPath $profileDirectory) { Write-Warning "The temporary Chrome profile is still at ${profileDirectory}; delete it once Chrome has exited." }
}
