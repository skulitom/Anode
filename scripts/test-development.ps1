#requires -Version 5.1
<# Tests commands and a headed browser exclusively in an existing seat.
   -InstallBrowserTools installs pinned Playwright into ignored artifacts; Chrome and Node must already exist.
#>
param([string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\output\playwright\development'),
    [switch]$InstallBrowserTools, [switch]$VerifyInput)
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$node = (Get-Command node -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runDirectory = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$dependencies = Join-Path $repo 'artifacts\development-browser-tools'
if ($InstallBrowserTools) {
    npm install --prefix $dependencies --no-audit --no-fund playwright@1.63.0 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Playwright install failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $dependencies 'node_modules\playwright'))) { throw 'Run with -InstallBrowserTools once.' }
function Invoke-AnodeJson([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $text = & $Anode @Arguments | Out-String
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Anode $($Arguments[0]) exited $LASTEXITCODE (expected $ExpectedExit): $text" }
    $text | ConvertFrom-Json
}
function Assert-That($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$before = Invoke-AnodeJson @('status','--json')
Assert-That ($before.state -eq 'ready' -and $before.session -ne $before.parentSession) 'An existing verified seat is required.'
$capabilities = Invoke-AnodeJson @('capabilities','--json')
Assert-That $capabilities.capture.available 'Seat capture must work before this visual test.'
$probe = Join-Path $runDirectory 'command-probe.ps1'
@'
[Console]::Out.WriteLine(('cwd=' + (Get-Location).Path))
[Console]::Out.WriteLine(('env=' + $env:ANODE_TEST_VALUE))
[Console]::Error.WriteLine('expected-test-stderr')
exit 7
'@ | Set-Content -LiteralPath $probe -Encoding UTF8
$result = Invoke-AnodeJson @('exec','--json','--cwd',$runDirectory,'--env','ANODE_TEST_VALUE=local to job','--wait','10000','--','powershell.exe','-NoProfile','-NonInteractive','-File',$probe) 7
Assert-That ($result.finished -and $result.exitCode -eq 7 -and $result.session -eq $before.session) 'Execution result or session incorrect.'
Assert-That ($result.stdout.Contains($runDirectory) -and $result.stdout.Contains('env=local to job') -and $result.stderr.Contains('expected-test-stderr')) 'Command streams, working directory or environment incorrect.'
$jobId = $null
try {
    $result = Invoke-AnodeJson @('exec','--json','--cwd',$repo,'--timeout','60000','--wait','0','--',$node,(Join-Path $repo 'examples\development\browser-check.cjs'),$dependencies,$runDirectory)
    $jobId = $result.jobId
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath (Join-Path $runDirectory 'browser-ready.json')) -and (Get-Date) -lt $deadline) {
        $result = Invoke-AnodeJson @('job',$jobId,'--json','--wait','500')
        if ($result.finished) { throw "Browser ended before the window check: $($result.stdout) $($result.stderr)" }
    }
    $browser = Get-Content -LiteralPath (Join-Path $runDirectory 'browser-ready.json') -Raw | ConvertFrom-Json
    Assert-That ((Get-Process -Id $browser.nodePid).SessionId -eq $before.session) 'Browser test runner is outside the seat.'
    $listed = Invoke-AnodeJson @('windows','--query','Anode development fixture','--json')
    $window = @($listed.windows | Where-Object { $_.process -eq 'chrome' }) | Select-Object -First 1
    Assert-That ($null -ne $window -and (Get-Process -Id $window.pid).SessionId -eq $before.session) 'Chrome window is outside the seat or missing.'
    $null = Invoke-AnodeJson @('window',$window.windowId,'raise','--json')
    if ($VerifyInput) { $null = Invoke-AnodeJson @('window',$window.windowId,'focus','--json') }
    $inspection = Join-Path $runDirectory 'inspection.html'
    $observed = Invoke-AnodeJson @('inspect',$window.windowId,'--max-depth','20','--max-elements','500','--html',$inspection,'--image',(Join-Path $runDirectory 'seat.png'),'--json')
    Assert-That (Test-Path -LiteralPath (Join-Path $runDirectory 'seat.png')) 'Fresh seat screenshot was not returned.'
    if ($VerifyInput) {
        $field = $observed.elements | Where-Object { $_.role -eq 'Edit' -and $_.name -eq 'Build name' } | Select-Object -First 1
        Assert-That ($field -and $field.bounds.width -gt 0 -and $field.bounds.height -gt 0) 'Visible input field bounds were not observed.'
        $x = [int]($field.bounds.x + $field.bounds.width / 2)
        $y = [int]($field.bounds.y + $field.bounds.height / 2)
        & $Anode click $x $y | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Seat mouse input failed.' }
        & $Anode key ctrl+a | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Seat key input failed.' }
        & $Anode type 'Anode raw input confirmed' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Seat text input failed.' }
        Set-Content -LiteralPath (Join-Path $runDirectory 'verify-input') -Value 'verify'
    }
    Set-Content -LiteralPath (Join-Path $runDirectory 'release-browser') -Value 'done'
    $result = Invoke-AnodeJson @('job',$jobId,'--json','--wait','10000')
    Assert-That ($result.finished -and $result.exitCode -eq 0) 'Browser test did not finish successfully.'
    $after = Invoke-AnodeJson @('status','--json')
    Assert-That ($after.session -eq $before.session -and $after.viewerVisible -eq $before.viewerVisible) 'Seat or viewer state changed.'
    $browserResult = $result.stdout | ConvertFrom-Json
    $report = [pscustomobject]@{passed=$true;session=$before.session;outputDirectory=$runDirectory;commandChecks=@('stdout and stderr','nonzero exit code','working directory','environment override','verified process session');browserChecks=$browserResult.checks;viewerUnchanged=$true;rawInputTested=[bool]$VerifyInput;inputWarning=$capabilities.input.warning}
    $report | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $runDirectory 'result.json')
} finally {
    if ($jobId) { & $Anode job $jobId --cancel --wait 3000 --json | Out-Null }
}
