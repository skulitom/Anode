#requires -Version 5.1
<#
.SYNOPSIS
Tests scripts\release-check.ps1 in dry runs. Needs no seat, lease or Anode build, and starts no Anode: every Anode
command and live script is a stub, and -Anode names a file that does not exist.
.DESCRIPTION
Runs release-check.ps1 -DryRun in child Windows PowerShell 5.1 processes and, when pwsh is on PATH (or -Pwsh names it),
PowerShell 7 processes: a default run, -SkipDisplay, failures on request (stop at the first failure, lease release,
summary still written), refused arguments and -Channel. It also runs it twice in this process to check that
ANODE_AGENT_ID, ANODE_LEASE_TOKEN and ANODE_CHANNEL come back as they were, and once in a shallow local clone
(git clone --depth 1 file://...), where the commit that set the version must be unknown. Output goes to
artifacts\release-check-test\<guid>, deleted at the end unless -Keep.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-release-check.ps1
#>
param([string]$Pwsh, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$releaseCheck = Join-Path $PSScriptRoot 'release-check.ps1'
$testRoot = Join-Path $root 'artifacts\release-check-test'
$workspace = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
$missingAnode = Join-Path $workspace 'missing\anode.exe'
$checkNames = @('desktop', 'chrome-form', 'development', 'explorer', 'display')
$environmentNames = @('ANODE_AGENT_ID', 'ANODE_LEASE_TOKEN', 'ANODE_CHANNEL')
$failures = New-Object Collections.Generic.List[string]
function Fail([string]$Message) { $failures.Add($Message); Write-Host "[fail] $Message" -ForegroundColor Red }
function Check([bool]$Condition, [string]$Message, [string]$Detail) {
    if ($Condition) { Write-Host "[ok] $Message" } else { Fail $(if ($Detail) { "$Message ($Detail)" } else { $Message }) }
}
# Native commands run under 'Continue': in 5.1, redirected stderr under 'Stop' is a terminating error.
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $text = (@(& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n")
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
    [pscustomobject]@{ ExitCode = $code; Text = $text }
}
function Invoke-Git([string[]]$Arguments) {
    $result = Invoke-Native 'git' $Arguments
    if ($result.ExitCode -ne 0) { throw "git $($Arguments -join ' ') failed: $($result.Text)" }
    $result.Text.Trim()
}
function Get-Environment { $values = @{}; foreach ($name in $environmentNames) { $values[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }; $values }
# Through Env:: PowerShell passes $null to SetEnvironmentVariable as '', which PowerShell 7's .NET keeps as an empty variable.
function Set-Environment($Values) {
    foreach ($name in $environmentNames) {
        if ($null -eq $Values[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue } else { Set-Item -LiteralPath "Env:$name" -Value $Values[$name] }
    }
}

# A run's exit code, output, evidence folder and summary (null when none was written).
function Read-Run([string]$Output, [int]$ExitCode, [string]$Text, [string]$Commit) {
    $directory = Join-Path $Output $Commit
    $summaryPath = Join-Path $directory 'summary.json'
    $json = if (Test-Path -LiteralPath $summaryPath) { [IO.File]::ReadAllText($summaryPath) } else { $null }
    $lines = @(if (Test-Path -LiteralPath (Join-Path $directory 'summary.txt')) { [IO.File]::ReadAllLines((Join-Path $directory 'summary.txt')) })
    [pscustomobject]@{ ExitCode = $ExitCode; Text = $Text; Output = $Output; Directory = $directory; Json = $json
        Summary = $(if ($json) { $json | ConvertFrom-Json } else { $null }); Lines = $lines }
}
function Invoke-Child([string]$Shell, [string]$Name, [string[]]$Arguments, [string]$Script = $releaseCheck, [string]$Commit = $commit) {
    $output = Join-Path $workspace $Name
    $result = Invoke-Native $Shell (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $Script, '-OutputDirectory', $output) + $Arguments)
    Read-Run $output $result.ExitCode $result.Text $Commit
}
function Invoke-InProcess([string]$Name, [hashtable]$Parameters) {
    $output = Join-Path $workspace $Name
    $text = (@(& $releaseCheck -OutputDirectory $output @Parameters *>&1 | ForEach-Object { "$_" }) -join "`n")
    Read-Run $output $LASTEXITCODE $text $commit
}
function Get-Statuses($Summary) { (@($Summary.checks | ForEach-Object { "$($_.name)=$($_.status)" }) -join ',') }
function Get-Check($Summary, [string]$Name) { @($Summary.checks | Where-Object { $_.name -eq $Name })[0] }
function Expect-Statuses([string[]]$Statuses) { (@(for ($i = 0; $i -lt $checkNames.Count; $i++) { "$($checkNames[$i])=$($Statuses[$i])" }) -join ',') }
function Read-Log($Summary, [string]$Name) {
    $log = (Get-Check $Summary $Name).log
    if ($log -and (Test-Path -LiteralPath $log)) { [IO.File]::ReadAllText($log) } else { '' }
}
# Every run that got as far as a summary: the fields that do not depend on the case.
function Test-Common([string]$Label, $Run, [int]$ExitCode) {
    $summary = $Run.Summary
    Check ($Run.ExitCode -eq $ExitCode) "$Label exits $ExitCode" "exit $($Run.ExitCode): $($Run.Text)"
    if (-not $summary) { Fail "$Label wrote no summary.json in $($Run.Directory)"; return $false }
    Check ($summary.tool -eq 'anode-release-check' -and $summary.schemaVersion -eq 1 -and $summary.dryRun -eq $true -and
        $summary.exitCode -eq $ExitCode -and $summary.passed -eq ($ExitCode -eq 0)) "$Label summary: dry run, exit code $ExitCode, passed $($ExitCode -eq 0)"
    Check ((@($summary.checks | ForEach-Object { $_.name }) -join ',') -eq ($checkNames -join ',')) "$Label summary lists the five checks in order"
    Check ($summary.commit -eq $commit -and $summary.version -eq $version -and $null -eq $summary.anodeVersion -and $null -eq $summary.anodeCommit -and
        $null -eq $summary.seatVersion -and $null -eq $summary.seat) "$Label summary names HEAD and the csproj version, and no Anode version, build commit or seat"
    Check ($Run.Json -match '"startedAt":\s*"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z"' -and $Run.Json -match '"finishedAt":\s*"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z"') `
        "$Label summary times are ISO 8601 UTC"
    if ($shallowCheckout) { Check ($null -eq $summary.candidateCommit -and $null -eq $summary.headIsCandidate) "$Label shallow checkout: candidateCommit null" }
    elseif ($summary.candidateCommit) { Check ($summary.headIsCandidate -eq ($summary.candidateCommit -eq $commit)) "$Label headIsCandidate matches candidateCommit" }
    Check ($Run.Lines.Count -gt 0 -and $Run.Lines[0] -match 'DRY RUN: not evidence for a release') "$Label summary.txt's first line says DRY RUN" "$(@($Run.Lines)[0])"
    $true
}

$savedEnvironment = Get-Environment
try {
    $commit = Invoke-Git @('-C', $root, 'rev-parse', 'HEAD')
    $shallowCheckout = (Invoke-Git @('-C', $root, 'rev-parse', '--is-shallow-repository')) -eq 'true'
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\Anode\Anode.csproj') -Raw
    $version = [string](@($project.Project.PropertyGroup.Version | Where-Object { $_ })[0])
    $null = New-Item -ItemType Directory -Path $workspace -Force
    # Child runs start from a known environment; the in-process runs below set their own values.
    Set-Environment @{}
    $shells = @([pscustomobject]@{ Label = '5.1'; Path = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') })
    if (-not $Pwsh) { $found = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1; if ($found) { $Pwsh = $found.Source } }
    if ($Pwsh -and (Test-Path -LiteralPath $Pwsh)) { $shells += [pscustomobject]@{ Label = '7'; Path = (Resolve-Path -LiteralPath $Pwsh).Path } }
    elseif ($Pwsh) { Fail "-Pwsh names no file: $Pwsh" }
    else { Write-Host '[skip] PowerShell 7 (pwsh) is not on PATH' }
    Write-Host "release-check.ps1 dry runs on $commit (Anode $version)" -ForegroundColor Cyan
    # The live run reads anode.exe, a GUI-subsystem program: Windows PowerShell 5.1 waits for one and captures its
    # output only through a pipe, and the dry run's stubs can't show that, so check the call itself.
    $nativeCall = [regex]::Match((Get-Content -LiteralPath $releaseCheck -Raw), '(?m)^\s*\$output = @\(& \$FilePath @Arguments 2>&1(.*)\)\s*$')
    Check ($nativeCall.Success -and $nativeCall.Groups[1].Value -match '^\s*\|') 'Invoke-Native pipes native output, so Windows PowerShell 5.1 waits for anode.exe' `
        $(if ($nativeCall.Success) { "after 2>&1: '$($nativeCall.Groups[1].Value)'" } else { 'the $output line was not found' })
    $dry = @('-DryRun', '-Anode', $missingAnode)

    foreach ($shell in $shells) {
        $label = "[$($shell.Label)]"
        $run = Invoke-Child $shell.Path "$($shell.Label)-default" $dry
        if (Test-Common "$label default" $run 0) {
            $summary = $run.Summary
            Check ((Get-Statuses $summary) -eq (Expect-Statuses @('passed', 'passed', 'passed', 'passed', 'passed')) -and
                @($summary.checks | Where-Object { $_.detail -ne 'dry run: not executed' }).Count -eq 0) "$label default: every check is a passed stub" (Get-Statuses $summary)
            Check ($summary.tagCommand -ceq "git tag v$version $commit") "$label default: tagCommand tags v$version at the full commit" "$($summary.tagCommand)"
            Check ($summary.lease.acquired -eq $true -and $summary.lease.released -eq $true) "$label default: lease acquired and released"
            Check ($null -eq $summary.channel -and $null -eq $summary.error -and $summary.anodePath -eq $missingAnode) "$label default: channel main, no error, -Anode recorded as given"
            Check (@($run.Lines -match ([regex]::Escape("git tag v$version $commit"))).Count -eq 1) "$label default: summary.txt has the tag line"
            $log = Read-Log $summary 'development'
            Check ($log.Contains('dry run: not executed') -and $log.Contains('test-development.ps1') -and $log.Contains("-Anode $missingAnode") -and
                $log.Contains('-InstallBrowserTools -VerifyInput') -and $log.Contains((Join-Path $run.Directory 'development'))) "$label default: development stub logs the script, -Anode, its output folder and switches"
            Check ($log.Contains("ANODE_AGENT_ID=release-check-$version") -and $log.Contains('ANODE_LEASE_TOKEN set') -and $log.Contains('ANODE_CHANNEL=(not set)')) `
                "$label default: checks see the agent id and lease token, and no channel"
            Check ((Read-Log $summary 'explorer').Contains('anode run explorer.exe C:\Windows')) "$label default: explorer stub logs its commands"
            Check (-not (Test-Path -LiteralPath $missingAnode)) "$label default: the nonexistent -Anode was never created or started"
        }

        $run = Invoke-Child $shell.Path "$($shell.Label)-skip-display" ($dry + '-SkipDisplay')
        if (Test-Common "$label -SkipDisplay" $run 0) {
            $display = Get-Check $run.Summary 'display'
            Check ((Get-Statuses $run.Summary) -eq (Expect-Statuses @('passed', 'passed', 'passed', 'passed', 'skipped')) -and $display.detail -eq '-SkipDisplay' -and $null -eq $display.log) `
                "$label -SkipDisplay: display skipped with reason -SkipDisplay; still passed" (Get-Statuses $run.Summary)
        }

        $run = Invoke-Child $shell.Path "$($shell.Label)-fail-development" ($dry + @('-DryRunFailAt', 'development'))
        if (Test-Common "$label -DryRunFailAt development" $run 1) {
            $summary = $run.Summary
            Check ((Get-Statuses $summary) -eq (Expect-Statuses @('passed', 'passed', 'failed', 'skipped', 'skipped'))) "$label -DryRunFailAt development: stops at development" (Get-Statuses $summary)
            Check ((Get-Check $summary 'development').detail -eq 'dry run: failed on request' -and
                @($summary.checks | Where-Object { $_.status -eq 'skipped' -and $_.detail -ne 'not run: an earlier check failed' }).Count -eq 0) "$label -DryRunFailAt development: failure and skip reasons"
            Check ($summary.lease.acquired -eq $true -and $summary.lease.released -eq $true -and $null -eq $summary.tagCommand) "$label -DryRunFailAt development: lease released, no tag"
            Check ($run.Lines[0] -match 'FAILED' -and @($run.Lines -match '^Tag').Count -eq 0) "$label -DryRunFailAt development: summary.txt says FAILED, no tag line"
        }

        $run = Invoke-Child $shell.Path "$($shell.Label)-fail-lease" ($dry + @('-DryRunFailAt', 'lease'))
        if (Test-Common "$label -DryRunFailAt lease" $run 2) {
            $summary = $run.Summary
            Check ((Get-Statuses $summary) -eq (Expect-Statuses @('skipped', 'skipped', 'skipped', 'skipped', 'skipped'))) "$label -DryRunFailAt lease: every check skipped" (Get-Statuses $summary)
            Check ($summary.lease.acquired -eq $false -and $summary.lease.released -eq $false -and $null -eq $summary.tagCommand -and $summary.error -match 'could not take the desktop') `
                "$label -DryRunFailAt lease: lease not acquired, reason recorded, no tag" "$($summary.error)"
        }

        $run = Invoke-Child $shell.Path "$($shell.Label)-fail-desktop" ($dry + @('-DryRunFailAt', 'desktop'))
        if (Test-Common "$label -DryRunFailAt desktop" $run 1) {
            Check ((Get-Statuses $run.Summary) -eq (Expect-Statuses @('failed', 'skipped', 'skipped', 'skipped', 'skipped')) -and $run.Summary.lease.released -eq $true) `
                "$label -DryRunFailAt desktop: the four later checks skipped, lease released" (Get-Statuses $run.Summary)
        }

        foreach ($refused in @(
            @{ Name = 'unknown-name'; Arguments = ($dry + @('-DryRunFailAt', 'nonsense')); What = '-DryRunFailAt nonsense' },
            @{ Name = 'fail-without-dry-run'; Arguments = @('-Anode', $missingAnode, '-DryRunFailAt', 'desktop'); What = '-DryRunFailAt without -DryRun' })) {
            $run = Invoke-Child $shell.Path "$($shell.Label)-$($refused.Name)" $refused.Arguments
            Check ($run.ExitCode -ne 0 -and $run.Text -match 'DryRunFailAt' -and -not (Test-Path -LiteralPath $run.Output)) `
                "$label $($refused.What) is refused before anything runs or is written" "exit $($run.ExitCode): $($run.Text)"
        }

        $run = Invoke-Child $shell.Path "$($shell.Label)-channel" ($dry + @('-Channel', 'dev'))
        if (Test-Common "$label -Channel dev" $run 0) {
            Check ($run.Summary.channel -eq 'dev' -and (Read-Log $run.Summary 'desktop').Contains('ANODE_CHANNEL=dev')) "$label -Channel dev: recorded, and the checks see it"
        }
    }

    # In this process, so a variable left behind would show: set values must come back, unset ones stay unset.
    $sentinels = @{ ANODE_AGENT_ID = 'sentinel-agent'; ANODE_LEASE_TOKEN = 'sentinel-token'; ANODE_CHANNEL = 'sentinel-channel' }
    Set-Environment $sentinels
    $run = Invoke-InProcess 'in-process' @{ DryRun = $true; Anode = $missingAnode; Channel = 'dev' }
    $after = Get-Environment
    Check (@($environmentNames | Where-Object { $after[$_] -cne $sentinels[$_] }).Count -eq 0) '[this process] set ANODE_* variables are unchanged after a run' (($environmentNames | ForEach-Object { "$_=$($after[$_])" }) -join ', ')
    if (Test-Common '[this process] -Channel dev' $run 0) {
        $log = Read-Log $run.Summary 'desktop'
        Check ($run.Summary.channel -eq 'dev' -and $log.Contains('ANODE_CHANNEL=dev') -and $log.Contains("ANODE_AGENT_ID=release-check-$version") -and $log.Contains('ANODE_LEASE_TOKEN set')) `
            '[this process] during the run the checks see -Channel, the agent id and a lease token'
    }
    Set-Environment @{}
    $run = Invoke-InProcess 'in-process' @{ DryRun = $true; Anode = $missingAnode; DryRunFailAt = 'explorer' }
    Check (@($environmentNames | Where-Object { Test-Path -LiteralPath "Env:$_" }).Count -eq 0) '[this process] unset ANODE_* variables stay unset after a failed run'
    if (Test-Common '[this process] -DryRunFailAt explorer' $run 1) {
        Check ((Get-Statuses $run.Summary) -eq (Expect-Statuses @('passed', 'passed', 'passed', 'failed', 'skipped')) -and $run.Summary.lease.released -eq $true) `
            '[this process] -DryRunFailAt explorer: stops at explorer, lease released' (Get-Statuses $run.Summary)
        $previous = @(Get-ChildItem -LiteralPath $run.Output -Directory | Where-Object { $_.Name -like "$commit-*" })
        Check ($previous.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $previous[0].FullName 'summary.json'))) "[this process] a second run on the commit keeps the first run's evidence beside it"
    }

    # A shallow clone, as CI checks out: git log -S cannot see the commit that set the version. Deleting a tracked
    # script there also covers a dirty dry run and a check that is not on the commit.
    $clone = Join-Path $workspace 'shallow-clone'
    $null = Invoke-Git @('clone', '--quiet', '--depth', '1', ([Uri]$root).AbsoluteUri, $clone)
    Copy-Item -LiteralPath $releaseCheck -Destination (Join-Path $clone 'scripts\release-check.ps1') -Force
    Remove-Item -LiteralPath (Join-Path $clone 'scripts\test-chrome-form.ps1')
    $cloneCommit = Invoke-Git @('-C', $clone, 'rev-parse', 'HEAD')
    $run = Invoke-Child $shells[0].Path 'shallow' $dry (Join-Path $clone 'scripts\release-check.ps1') $cloneCommit
    $summary = $run.Summary
    Check ($run.ExitCode -eq 0 -and $summary -and $summary.commit -eq $cloneCommit) '[shallow clone] dry run exits 0 and names its HEAD' "exit $($run.ExitCode): $($run.Text)"
    if ($summary) {
        Check ($null -eq $summary.candidateCommit -and $null -eq $summary.headIsCandidate -and @(@($summary.notes) -match 'shallow').Count -eq 1) '[shallow clone] candidateCommit and headIsCandidate are null, with a note'
        $chrome = Get-Check $summary 'chrome-form'
        Check ($summary.dirty -eq $true -and $summary.passed -eq $true -and $chrome.status -eq 'skipped' -and $chrome.detail -eq 'not on this commit') `
            '[shallow clone] a dirty dry run carries on; a check missing from the commit is skipped, not failed' "dirty $($summary.dirty), chrome-form $($chrome.status): $($chrome.detail)"
    }
} catch {
    Fail "The test itself failed: $($_.Exception.Message)"
} finally {
    Set-Environment $savedEnvironment
    if ($Keep) { Write-Host "Kept $workspace" }
    elseif (Test-Path -LiteralPath $workspace) {
        Remove-Item -LiteralPath $workspace -Recurse -Force
        if (-not (Get-ChildItem -LiteralPath $testRoot -Force)) { Remove-Item -LiteralPath $testRoot -Force }
    }
}
if ($failures.Count) { throw "$($failures.Count) release-check test(s) failed. Run with -Keep to keep the output." }
Write-Host 'All release-check dry-run tests passed.' -ForegroundColor Green
