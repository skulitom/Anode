#requires -Version 5.1
<#
.SYNOPSIS
Tests desktop tools against a disposable app in an already running Anode seat.
Never starts a seat or activates the parent viewer. No physical or virtual gamepad input.
#>
param([string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'), [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\desktop-test'))
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
function Call-Anode([string[]]$Arguments) {
    if ($Arguments[0] -in @('windows','inspect','window','element') -and $Arguments -notcontains '--json') { $Arguments += '--json' }
    $resultText = (& $Anode @Arguments | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Anode command failed: $($Arguments[0])" }
    return $resultText | ConvertFrom-Json
}
function Assert-That($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$status = Call-Anode @('status', '--json')
Assert-That ($status.state -eq 'ready' -and $status.session -ne $status.parentSession) 'An existing verified seat is required.'
$launched = Call-Anode @('run', $Anode, '__desktop-fixture')
$fixturePid = [int]$launched.pid
Assert-That ($launched.session -eq $status.session) 'Fixture launched in the wrong session.'
$fixture = Get-Process -Id $fixturePid
$fixtureStarted = $fixture.StartTime
$windowId = $null
try {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $listed = Call-Anode @('windows', '--pid', "$fixturePid", '--json')
        $windows = @($listed.windows)
        if ($windows.Count -eq 0) { Start-Sleep -Milliseconds 200 }
    } while ($windows.Count -eq 0 -and (Get-Date) -lt $deadline)
    Assert-That ($windows.Count -eq 1) 'Fixture did not expose exactly one window.'
    $windowId = $windows[0].windowId
    function Observe-Fixture { Call-Anode @('inspect', $windowId, '--json') }
    $observation = Observe-Fixture
    Assert-That (($observation | ConvertTo-Json -Depth 30) -notmatch 'fixture-secret-must-not-be-exported') 'Password text leaked into the observation.'
    $password = @($observation.elements | Where-Object { $_.password })
    Assert-That ($password.Count -eq 1 -and @($password[0].actions).Count -eq 0) 'Password control was not protected.'
    $field = $observation.elements | Where-Object { $_.automationId -eq 'DraftNote' } | Select-Object -First 1
    Assert-That ($null -ne $field) 'Editable note was missing from the tree.'
    $consumed = $observation.snapshotId
    $null = Call-Anode @('element', $consumed, $field.id, 'set_value', '--value', 'Anode background test')
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $replay = (& $Anode element $consumed $field.id set_value --value 'must not apply' 2>&1 | Out-String)
        $replayExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    Assert-That ($replayExitCode -ne 0 -and $replay -match 'already used|Unknown|expired') 'Consumed observation was reused.'
    $observation = Observe-Fixture
    $field = $observation.elements | Where-Object { $_.automationId -eq 'DraftNote' } | Select-Object -First 1
    Assert-That ($field.text -eq 'Anode background test') 'Text replacement was not observed.'
    $button = $observation.elements | Where-Object { $_.automationId -eq 'ApplyNote' } | Select-Object -First 1
    $null = Call-Anode @('element', $observation.snapshotId, $button.id, 'invoke')
    $observation = Observe-Fixture
    $label = $observation.elements | Where-Object { $_.automationId -eq 'Result' } | Select-Object -First 1
    Assert-That ($label.name -eq 'Applied: Anode background test') 'Button invocation did not update the result.'
    $checkbox = $observation.elements | Where-Object { $_.automationId -eq 'Preview' } | Select-Object -First 1
    $null = Call-Anode @('element', $observation.snapshotId, $checkbox.id, 'toggle')
    $observation = Observe-Fixture
    $checkbox = $observation.elements | Where-Object { $_.automationId -eq 'Preview' } | Select-Object -First 1
    Assert-That ($checkbox.toggleState -eq 'On') 'Checkbox toggle was not observed.'
    $choice = $observation.elements | Where-Object { $_.role -eq 'ListItem' -and $_.name -eq 'Beta' } | Select-Object -First 1
    $null = Call-Anode @('element', $observation.snapshotId, $choice.id, 'select')
    $observation = Observe-Fixture
    $choice = $observation.elements | Where-Object { $_.role -eq 'ListItem' -and $_.name -eq 'Beta' } | Select-Object -First 1
    Assert-That ($choice.selected -eq $true) 'List selection was not observed.'
    $level = $observation.elements | Where-Object { $_.automationId -eq 'Level' } | Select-Object -First 1
    Assert-That ($level.actions -contains 'set_value') 'Windows Forms slider did not expose its value action.'
    $null = Call-Anode @('element', $observation.snapshotId, $level.id, 'set_value', '--value', '45')
    $observation = Observe-Fixture
    $level = $observation.elements | Where-Object { $_.automationId -eq 'Level' } | Select-Object -First 1
    Assert-That ($level.text -eq '45') 'Slider text value was not observed.'
    $level = $observation.elements | Where-Object { $_.automationId -eq 'RangeLevel' } | Select-Object -First 1
    Assert-That ($level.range.minimum -eq 0 -and $level.range.maximum -eq 100) 'Slider range was missing.'
    $null = Call-Anode @('element', $observation.snapshotId, $level.id, 'set_range', '--number', '60')
    $observation = Observe-Fixture
    $level = $observation.elements | Where-Object { $_.automationId -eq 'RangeLevel' } | Select-Object -First 1
    Assert-That ($level.range.value -eq 60) 'Slider value was not observed.'
    $limited = Call-Anode @('inspect', $windowId, '--max-text', '0')
    $readOnly = $limited.elements | Where-Object { $_.automationId -eq 'ReadOnlyNote' } | Select-Object -First 1
    Assert-That ($readOnly.readOnly -eq $true -and $readOnly.actions -notcontains 'set_value') 'Read-only control offered an edit with zero text budget.'
    $null = Call-Anode @('window', $windowId, 'move', '--x', '120', '--y', '100', '--width', '620', '--height', '460')
    $observation = Observe-Fixture
    Assert-That ($observation.window.bounds.x -eq 120 -and $observation.window.bounds.y -eq 100) 'Window move was not observed.'
    $report = Join-Path $OutputDirectory 'inspection.html'
    $null = Call-Anode @('inspect', $windowId, '--html', $report, '--json')
    $finalStatus = Call-Anode @('status', '--json')
    Assert-That ($finalStatus.session -eq $status.session -and $finalStatus.viewerVisible -eq $status.viewerVisible) 'Seat or parent viewer state changed.'
    [pscustomobject]@{ passed=$true; session=$status.session; fixturePid=$fixturePid; report=$report; checks=@('window discovery','control tree','password omission','text replacement','stale-reference refusal','button invocation','checkbox toggle','list selection','slider range and value','read-only action filtering','window move','HTML report','parent viewer unchanged') } |
        ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($windowId) { & $Anode window $windowId close | Out-Host }
    $remaining = Get-Process -Id $fixturePid -ErrorAction SilentlyContinue
    if ($remaining -and $remaining.SessionId -eq $status.session -and $remaining.StartTime -eq $fixtureStarted) {
        if (-not $remaining.WaitForExit(2000)) { & $Anode ps kill $fixturePid | Out-Host }
    }
}
