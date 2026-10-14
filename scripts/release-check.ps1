#requires -Version 5.1
<#
.SYNOPSIS
Runs the release's live check on the checked-out candidate as one command and writes evidence that names the commit.
.DESCRIPTION
For the owner, once per release, on a real seat that is free for testing. Checks the checkout and the Anode under
test, takes the desktop lease (which may start the daemon and a hidden seat), then runs these checks in order and stops
at the first failure: test-desktop.ps1, test-chrome-form.ps1, test-development.ps1 -InstallBrowserTools -VerifyInput,
inspect on an owned File Explorer window, and test-display.ps1, which changes the seat's display (-SkipDisplay skips it).
The first development run installs Playwright under artifacts (network). The lease is released and ANODE_AGENT_ID,
ANODE_LEASE_TOKEN and ANODE_CHANNEL are restored even when a step fails.
Evidence goes to <OutputDirectory>\<commit>: summary.json, summary.txt (also printed; paste it into the release record)
and one log per check. A previous run's folder for the same commit is renamed, never overwritten.
-DryRun stubs every Anode command and live script, so scripts\test-release-check.ps1 can test the orchestration
without a seat. Exit codes: 0 every check passed or was skipped on purpose; 1 a check failed; 2 a precondition or the
lease failed, or the arguments were invalid (then nothing runs and no summary is written).
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release-check.ps1
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release-check.ps1 -Anode artifacts\pkg-build\anode.exe -Channel dev
#>
param(
    [string]$Anode,
    [string]$Channel,
    [switch]$SkipDisplay,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release-check'),
    [string]$AgentId,
    [switch]$DryRun,
    [string]$DryRunFailAt
)
$ErrorActionPreference = 'Stop'
$checkNames = @('desktop', 'chrome-form', 'development', 'explorer', 'display')
function Stop-Usage([string]$Message) { [Console]::Error.WriteLine("release-check: $Message"); exit 2 }
if ($PSBoundParameters.ContainsKey('DryRunFailAt')) {
    if (-not $DryRun) { Stop-Usage '-DryRunFailAt works only with -DryRun.' }
    if ($DryRunFailAt -notin (@('lease') + $checkNames)) { Stop-Usage "-DryRunFailAt takes lease or a check name ($($checkNames -join ', ')), not '$DryRunFailAt'." }
    $DryRunFailAt = $DryRunFailAt.ToLowerInvariant()
}
if ($Channel) {
    $Channel = $Channel.Trim().ToLowerInvariant()
    if ($Channel -notmatch '^[a-z0-9][a-z0-9-]{0,31}$') { Stop-Usage "-Channel takes 1-32 letters, digits or hyphens, such as dev, not '$Channel'." }
}
if ($AgentId -and $AgentId -notmatch '^[A-Za-z0-9._-]{1,80}$') { Stop-Usage "-AgentId takes 1-80 letters, digits, '.', '-' or '_', not '$AgentId'." }

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$utf8 = New-Object Text.UTF8Encoding $false
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell)) { $windowsPowerShell = 'powershell.exe' }
$environmentNames = @('ANODE_AGENT_ID', 'ANODE_LEASE_TOKEN', 'ANODE_CHANNEL')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

$started = [DateTime]::UtcNow
$notes = New-Object Collections.Generic.List[string]
$checks = New-Object Collections.Generic.List[object]
foreach ($name in $checkNames) { $checks.Add([ordered]@{ name = $name; status = 'pending'; startedAt = $null; seconds = $null; detail = $null; log = $null }) }
$summary = [ordered]@{
    tool = 'anode-release-check'; schemaVersion = 1; dryRun = [bool]$DryRun; passed = $false; exitCode = $null
    commit = $null; candidateCommit = $null; headIsCandidate = $null; dirty = $null; version = $null; anodeVersion = $null
    anodePath = $null; channel = $null; windows = [Environment]::OSVersion.Version.ToString(3)
    startedAt = $started.ToString('o', $invariant); finishedAt = $null; seat = $null
    lease = [ordered]@{ acquired = $false; released = $false }; checks = $null; tagCommand = $null; error = $null; notes = $null
}
$exitCode = 0
$skipReason = 'not run: a precondition failed'
$checksStarted = $false
$completed = $false
$shallow = $false
$version = $null
$anodePath = $null
$commitDirectory = Join-Path $OutputDirectory 'no-commit'

# Ends the run before or instead of the checks: exit code 2 with a one-line reason.
function Stop-Run([string]$Message) { $script:exitCode = 2; $summary.error = $Message; throw $Message }
# The first non-blank line of the given texts, for one-line reasons.
function Get-FirstText([string[]]$Values) { @($Values | ForEach-Object { "$_" -split "`r?`n" } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -First 1 }
function Format-Command([string]$FilePath, [string[]]$Arguments) {
    (@($FilePath) + $Arguments | ForEach-Object { if ($_ -eq '' -or $_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
}
function New-LogWriter([string]$Path) { $writer = New-Object IO.StreamWriter($Path, $false, $utf8); $writer.AutoFlush = $true; $writer }

# Native commands: under 'Stop', Windows PowerShell 5.1 turns redirected stderr into terminating errors,
# so run them under 'Continue' and judge them by the exit code.
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
    [pscustomobject]@{
        ExitCode = $code
        Output = (@($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join [Environment]::NewLine)
        Errors = @($output | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    }
}
function Invoke-Git([string[]]$Arguments) {
    $result = Invoke-Native 'git' (@('-C', $repo) + $Arguments)
    if ($result.ExitCode -ne 0) { Stop-Run "git $($Arguments -join ' ') failed: $(Get-FirstText (@($result.Errors) + "exit $($result.ExitCode)"))" }
    $result.Output.Trim()
}
function Invoke-Anode([string[]]$Arguments, [IO.TextWriter]$Log) {
    $result = Invoke-Native $anodePath $Arguments
    if ($Log) {
        $Log.WriteLine('> anode ' + (Format-Command $Arguments[0] @($Arguments | Select-Object -Skip 1)))
        if ($result.Output.Trim()) { $Log.WriteLine($result.Output.TrimEnd()) }
        foreach ($line in $result.Errors) { $Log.WriteLine($line) }
        $Log.WriteLine("(exit $($result.ExitCode))")
    }
    $result
}
function Invoke-AnodeJson([string[]]$Arguments, [IO.TextWriter]$Log) {
    $result = Invoke-Anode $Arguments $Log
    if ($result.ExitCode -ne 0) { throw "anode $($Arguments[0]) exited $($result.ExitCode): $(Get-FirstText (@($result.Errors) + $result.Output))" }
    try { $result.Output | ConvertFrom-Json } catch { throw "anode $($Arguments[0]) did not print JSON: $(Get-FirstText @($result.Output))" }
}
function Find-Chrome {
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ($root -and (Test-Path -LiteralPath (Join-Path $root 'Google\Chrome\Application\chrome.exe'))) { return $true }
    }
    $false
}

# A live script runs in Windows PowerShell 5.1, as the owner runs it, with its output shown and logged as it comes.
# It passes when it exits 0 and the result.json it wrote during this run says "passed": true.
function Invoke-ScriptCheck($Check, [string[]]$Arguments) {
    $since = [DateTime]::UtcNow.AddSeconds(-2)
    $writer = New-LogWriter $Check.log
    $state = @{ FirstError = $null }
    $saved = $ErrorActionPreference
    try {
        $writer.WriteLine('> ' + (Format-Command $windowsPowerShell $Arguments))
        $ErrorActionPreference = 'Continue'
        & $windowsPowerShell @Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            if ($_ -is [Management.Automation.ErrorRecord] -and -not $state.FirstError -and $line.Trim()) { $state.FirstError = $line.Trim() }
            $writer.WriteLine($line)
            Write-Host $line
        }
        $code = $LASTEXITCODE
        $writer.WriteLine("(exit $code)")
    } finally { $ErrorActionPreference = $saved; $writer.Dispose() }
    if ($code -ne 0) { throw (Get-FirstText @($state.FirstError, "exited $code")) }
    $resultFile = @(Get-ChildItem -LiteralPath (Join-Path $commitDirectory $Check.name) -Filter 'result.json' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $since } | Sort-Object LastWriteTimeUtc) | Select-Object -Last 1
    if (-not $resultFile) { throw 'it exited 0 but wrote no result.json' }
    $result = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json
    if ($result.passed -ne $true) { throw "its result does not say ""passed"": true ($($resultFile.FullName))" }
    switch ($Check.name) {
        'desktop' { "$(@($result.checks).Count) checks" }
        'chrome-form' {
            $warnings = @($result.warnings | Where-Object { $_ })
            if ($warnings.Count) { "warning: $($warnings -join '; ')" } else { "Chrome $($result.chromeVersion), no warnings" }
        }
        'development' {
            $text = if ($result.rawInputTested) { 'input delivery confirmed' } else { 'input not tested' }
            if ($result.inputWarning) { $text += "; warning: $($result.inputWarning)" }
            $text
        }
        'display' { "$(@($result.displays).Count) displays, reset to $($result.startup)" }
    }
}

# File Explorer's inspect, which failed in 0.11.1. Only the window this check opened is closed.
$explorerPlan = @('anode windows --query Windows --json', 'anode run explorer.exe C:\Windows',
    'anode windows --query Windows --json, every 500 ms for up to 15 s, until a new window of process explorer appears',
    'anode inspect <new window> --json', 'anode window <new window> close')
function Invoke-ExplorerCheck($Check) {
    $writer = New-LogWriter $Check.log
    try {
        $before = @((Invoke-AnodeJson @('windows', '--query', 'Windows', '--json') $writer).windows | ForEach-Object { [string]$_.windowId })
        $ran = Invoke-Anode @('run', 'explorer.exe', 'C:\Windows') $writer
        if ($ran.ExitCode -ne 0) { throw "anode run explorer.exe exited $($ran.ExitCode): $(Get-FirstText (@($ran.Errors) + $ran.Output))" }
        $windowId = $null
        try {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while ($true) {
                $new = @((Invoke-AnodeJson @('windows', '--query', 'Windows', '--json') $writer).windows |
                    Where-Object { $_ -and $_.process -eq 'explorer' -and [string]$_.windowId -notin $before })
                if ($new.Count -gt 0 -or $watch.Elapsed.TotalSeconds -ge 15) { break }
                Start-Sleep -Milliseconds 500
            }
            if ($new.Count -eq 0) {
                throw 'no new File Explorer window appeared in the seat within 15 s; an Explorer window already open there may have taken the folder as a tab: close it and run again'
            }
            $windowId = [string]$new[0].windowId
            # Observing again is safe; a fresh window can take a moment to fill its tree. An error fails at once.
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                $count = @((Invoke-AnodeJson @('inspect', $windowId, '--json') $writer).elements | Where-Object { $_ }).Count
                if ($count -gt 0) { break }
                Start-Sleep -Seconds 1
            }
            if ($count -lt 1) { throw "inspect listed no controls in the new File Explorer window ($windowId)" }
            "$count controls"
        } finally {
            if ($windowId) {
                $closed = Invoke-Anode @('window', $windowId, 'close') $writer
                if ($closed.ExitCode -ne 0) { $notes.Add("The File Explorer window $windowId this check opened was not closed: $(Get-FirstText (@($closed.Errors) + $closed.Output))") }
            }
        }
    } finally { $writer.Dispose() }
}

function Invoke-Check($Check, [string[]]$Arguments) {
    $Check.startedAt = [DateTime]::UtcNow.ToString('o', $invariant)
    $Check.log = Join-Path $commitDirectory "$($Check.name).log"
    Write-Host "== $($Check.name) ==" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if ($DryRun) {
            $plan = if ($Check.name -eq 'explorer') { $explorerPlan } else { Format-Command $windowsPowerShell $Arguments }
            $environment = 'environment: ' + (@($environmentNames | ForEach-Object {
                $value = [Environment]::GetEnvironmentVariable($_, 'Process')
                if ($_ -eq 'ANODE_LEASE_TOKEN') { "$_ " + $(if ($value) { 'set' } else { 'not set' }) } else { "$_=" + $(if ($value) { $value } else { '(not set)' }) }
            }) -join ', ')
            [IO.File]::WriteAllLines($Check.log, [string[]](@('dry run: not executed') + $plan + $environment), $utf8)
            if ($DryRunFailAt -eq $Check.name) { throw 'dry run: failed on request' }
            $detail = 'dry run: not executed'
        } elseif ($Check.name -eq 'explorer') {
            $detail = Invoke-ExplorerCheck $Check
        } else {
            $detail = Invoke-ScriptCheck $Check $Arguments
        }
        $Check.status = 'passed'
        $Check.detail = [string]$detail
    } catch {
        $Check.status = 'failed'
        $Check.detail = $_.Exception.Message
        $script:exitCode = 1
    } finally { $Check.seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1) }
    Write-Host "$($Check.name): $($Check.status), $($Check.detail)"
}

function Format-Summary {
    $lines = New-Object Collections.Generic.List[string]
    $verdict = if ($summary.passed) { 'PASSED' } else { "FAILED (exit $($summary.exitCode))" }
    if ($DryRun) { $verdict += ' (DRY RUN: not evidence for a release)' }
    $lines.Add("Anode release check: $verdict")
    if ($summary.error) { $lines.Add("Stopped: $($summary.error)") }
    $commitText = if ($summary.commit) { $summary.commit } else { 'unknown' }
    if ($version) {
        $setBy = if ($summary.headIsCandidate -is [bool] -and $summary.headIsCandidate) { 'yes' }
            elseif ($summary.headIsCandidate -is [bool]) { "no, that is $($summary.candidateCommit.Substring(0, 7))" }
            elseif ($shallow) { 'unknown in a shallow clone' } else { 'unknown' }
        $commitText += " (the commit that set ${version}: $setBy)"
    }
    if ($summary.dirty) { $commitText += ', with uncommitted changes' }
    $lines.Add("Commit $commitText")
    $anodeText = if ($DryRun) { "Anode $version (checkout), not started" }
        else { "Anode $(Get-FirstText @($summary.anodeVersion, '?')), $(Get-FirstText @($summary.anodePath, 'not found'))" }
    $channelText = Get-FirstText @($summary.channel, 'main')
    $seatText = if ($summary.seat -and $null -ne $summary.seat.session) { "seat session $($summary.seat.session)" } else { 'no seat' }
    $lines.Add("$anodeText, channel $channelText, Windows $($summary.windows), $seatText")
    $lines.Add($started.ToString('yyyy-MM-dd HH:mm:ss', $invariant) + 'Z to ' +
        $finished.ToString($(if ($finished.Date -eq $started.Date) { 'HH:mm:ss' } else { 'yyyy-MM-dd HH:mm:ss' }), $invariant) + 'Z')
    foreach ($check in $checks) {
        $seconds = if ($null -ne $check.seconds) { '{0,3} s' -f [int][Math]::Round($check.seconds) } else { '     ' }
        $lines.Add(('{0,-13} {1,-8}{2}   {3}' -f $check.name, $check.status, $seconds, $check.detail).TrimEnd())
    }
    foreach ($note in $notes) { $lines.Add("Note: $note") }
    if ($summary.tagCommand) {
        $prefix = if ($DryRun) { 'Tag (dry run, do not use): ' } else { 'Tag: ' }
        $lines.Add("$prefix$($summary.tagCommand)  then  git push origin v$version")
    }
    $lines.ToArray()
}

try {
    if ($Channel) { $env:ANODE_CHANNEL = $Channel }
    $effectiveChannel = Get-FirstText @($env:ANODE_CHANNEL)
    if ($effectiveChannel -and $effectiveChannel.ToLowerInvariant() -ne 'main') { $summary.channel = $effectiveChannel.ToLowerInvariant() }

    # 1. The commit. Live evidence must name a commit, so tracked changes are refused there.
    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { Stop-Run 'git is not on PATH.' }
    $commit = Invoke-Git @('rev-parse', 'HEAD')
    if ($commit -notmatch '^[0-9a-f]{40,64}$') { Stop-Run "git rev-parse HEAD printed '$commit', not a commit." }
    $summary.commit = $commit
    $commitDirectory = Join-Path $OutputDirectory $commit
    if (Test-Path -LiteralPath $commitDirectory) {
        $stamp = (Get-Item -LiteralPath $commitDirectory).LastWriteTimeUtc.ToString('yyyyMMdd-HHmmss', $invariant)
        $previous = "$commitDirectory-$stamp"
        for ($n = 2; Test-Path -LiteralPath $previous; $n++) { $previous = "$commitDirectory-$stamp-$n" }
        Move-Item -LiteralPath $commitDirectory -Destination $previous
        Write-Host "The previous run's evidence for this commit is now in $previous"
    }
    $null = New-Item -ItemType Directory -Path $commitDirectory -Force
    $summary.dirty = [bool](Invoke-Git @('status', '--porcelain', '--untracked-files=no'))
    if ($summary.dirty -and -not $DryRun) { Stop-Run 'commit or stash first: the evidence must name a commit.' }

    # 2. The version the checkout builds, read as build.yml reads it.
    [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src\Anode\Anode.csproj') -Raw
    $version = [string](@($project.Project.PropertyGroup.Version | Where-Object { $_ })[0])
    if ($version -notmatch '^\d+\.\d+\.\d+$') { Stop-Run "src\Anode\Anode.csproj has no X.Y.Z <Version> (found '$version')." }
    $summary.version = $version

    # 3. The commit that set this version. git log -S cannot see history in a shallow clone, as in CI.
    $shallow = (Invoke-Git @('rev-parse', '--is-shallow-repository')) -eq 'true'
    if ($shallow) {
        $notes.Add("This is a shallow clone, so the commit that set $version is unknown.")
    } else {
        $candidate = Invoke-Git @('log', '-1', '--format=%H', "-S<Version>$version</Version>", '--', 'src/Anode/Anode.csproj')
        if ($candidate) {
            $summary.candidateCommit = $candidate
            $summary.headIsCandidate = $candidate -eq $commit
            if ($candidate -ne $commit) { $notes.Add("HEAD is not the commit that set $version ($($candidate.Substring(0, 7))); a tag here releases the commits after it too.") }
        } else { $notes.Add("No commit in this history sets $version in src/Anode/Anode.csproj.") }
    }

    # 4 and 5. The Anode under test and the tools the live scripts need. A dry run never resolves or starts Anode.
    if ($DryRun) {
        if ($Anode) { $summary.anodePath = $Anode }
        $scriptAnode = Get-FirstText @($Anode, 'anode')
    } else {
        if ($Anode) {
            if (-not (Test-Path -LiteralPath $Anode -PathType Leaf)) { Stop-Run "the Anode under test was not found: $Anode" }
            $anodePath = (Resolve-Path -LiteralPath $Anode).Path
        } else {
            $command = Get-Command anode -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $command) { Stop-Run 'anode is not on PATH: install the candidate, or pass -Anode with the candidate build.' }
            $anodePath = $command.Source
        }
        $summary.anodePath = $anodePath
        # The live scripts start this file as their fixture too, so it must be anode.exe itself, not a wrapper.
        if ([IO.Path]::GetExtension($anodePath) -ne '.exe') { Stop-Run "the Anode under test must be an .exe: $anodePath" }
        $scriptAnode = $anodePath
        $printed = Invoke-Anode @('version')
        $line = $printed.Output.Trim()
        $summary.anodeVersion = if ($line -match '^anode (\S+)$') { $Matches[1] } elseif ($line) { $line } else { $null }
        if ($printed.ExitCode -ne 0 -or $line -cne "anode $version") {
            Stop-Run "the Anode under test is $(Get-FirstText @($summary.anodeVersion, 'unknown')), the checkout is ${version}: install the candidate, or pass -Anode with the candidate build."
        }
        if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) { Stop-Run 'Node.js is not on PATH; test-development.ps1 needs it.' }
        if (-not (Find-Chrome)) { Stop-Run 'Chrome was not found in Program Files, Program Files (x86) or LocalAppData; the Chrome checks need it.' }
    }

    # The lease. Acquiring may start the daemon and a hidden seat; that is intended here.
    $skipReason = 'not run: the desktop lease failed'
    $env:ANODE_AGENT_ID = Get-FirstText @($AgentId, "release-check-$version")
    $env:ANODE_LEASE_TOKEN = $null
    if ($DryRun) {
        if ($DryRunFailAt -eq 'lease') { Stop-Run 'could not take the desktop: dry run: failed on request' }
        $env:ANODE_LEASE_TOKEN = 'dry-run'
        $summary.lease.acquired = $true
    } else {
        $acquired = Invoke-Anode @('lease', 'acquire', '--ttl', '600')
        $token = $null
        if ($acquired.ExitCode -eq 0) { try { $token = ($acquired.Output | ConvertFrom-Json).leaseToken } catch { $token = $null } }
        if (-not $token) { Stop-Run "could not take the desktop: $(Get-FirstText (@($acquired.Errors) + $acquired.Output + "exit $($acquired.ExitCode)"))" }
        $env:ANODE_LEASE_TOKEN = $token
        $summary.lease.acquired = $true
        $skipReason = 'not run: the seat was not ready'
        $reported = Invoke-Anode @('status', '--json')
        $status = $null
        try { $status = $reported.Output | ConvertFrom-Json } catch { $status = $null }
        if ($status) {
            $summary.seat = [ordered]@{ session = $status.session; parentSession = $status.parentSession; state = $status.state; viewerVisible = $status.viewerVisible }
            if ($status.channel) { $summary.channel = if ($status.channel -eq 'main') { $null } else { [string]$status.channel } }
        }
        if (-not $status -or $status.state -ne 'ready' -or $null -eq $status.session -or $status.session -eq $status.parentSession) {
            $state = if ($status -and $status.state) { $status.state } else { 'unknown' }
            $start = if ($summary.channel) { "anode --channel $($summary.channel) start --hidden" } else { 'anode start --hidden' }
            Stop-Run "the seat isn't ready (state $state): start it (``$start``), sign in if Windows asks, then run this again."
        }
    }

    # The checks, in order; the first failure skips the rest.
    $checksStarted = $true
    $skipReason = 'not run: an earlier check failed'
    $scripts = @{ 'desktop' = 'test-desktop.ps1'; 'chrome-form' = 'test-chrome-form.ps1'; 'development' = 'test-development.ps1'; 'display' = 'test-display.ps1' }
    foreach ($check in $checks) {
        if ($exitCode -ne 0) { break }
        if ($check.name -eq 'display' -and $SkipDisplay) { $check.status = 'skipped'; $check.detail = '-SkipDisplay'; continue }
        if ($check.name -eq 'explorer') { Invoke-Check $check @(); continue }
        $scriptPath = Join-Path $repo "scripts\$($scripts[$check.name])"
        if ($check.name -eq 'chrome-form' -and -not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { $check.status = 'skipped'; $check.detail = 'not on this commit'; continue }
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath, '-Anode', $scriptAnode, '-OutputDirectory', (Join-Path $commitDirectory $check.name))
        if ($check.name -eq 'development') { $arguments += @('-InstallBrowserTools', '-VerifyInput') }
        Invoke-Check $check $arguments
    }
    $completed = $true
} catch {
    if (-not $summary.error) {
        $summary.error = $_.Exception.Message
        $exitCode = if ($checksStarted) { 1 } else { 2 }
    }
} finally {
    if ($summary.lease.acquired) {
        if ($DryRun) { $summary.lease.released = $true }
        else {
            try {
                $released = Invoke-Anode @('lease', 'release')
                $summary.lease.released = $released.ExitCode -eq 0
                if (-not $summary.lease.released) { $notes.Add("The lease was not released ($(Get-FirstText (@($released.Errors) + $released.Output + "exit $($released.ExitCode)"))); it ends 10 minutes after the last desktop command.") }
            } catch { $notes.Add("The lease was not released ($($_.Exception.Message)); it ends 10 minutes after the last desktop command.") }
        }
    }
    # Through Env:: PowerShell passes $null to SetEnvironmentVariable as '', which PowerShell 7's .NET keeps as an empty variable.
    foreach ($name in $environmentNames) {
        if ($null -eq $savedEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $savedEnvironment[$name] }
    }
    if (-not $completed -and -not $summary.error) {
        $summary.error = 'The run was interrupted.'
        if ($exitCode -eq 0) { $exitCode = if ($checksStarted) { 1 } else { 2 } }
    }
    foreach ($check in $checks) {
        if ($check.status -ne 'pending') { continue }
        if ($check.startedAt) { $check.status = 'failed'; $check.detail = 'interrupted' }
        else { $check.status = 'skipped'; $check.detail = $skipReason }
    }
    $summary.exitCode = $exitCode
    $summary.passed = $exitCode -eq 0
    if ($summary.passed) { $summary.tagCommand = "git tag v$version $($summary.commit)" }
    $finished = [DateTime]::UtcNow
    $summary.finishedAt = $finished.ToString('o', $invariant)
    $summary.checks = $checks.ToArray()
    $summary.notes = $notes.ToArray()
    $null = New-Item -ItemType Directory -Path $commitDirectory -Force
    [IO.File]::WriteAllText((Join-Path $commitDirectory 'summary.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
    $text = Format-Summary
    [IO.File]::WriteAllLines((Join-Path $commitDirectory 'summary.txt'), [string[]]$text, $utf8)
    Write-Host ''
    Write-Host $text[0] -ForegroundColor $(if ($summary.passed) { 'Green' } else { 'Red' })
    foreach ($line in @($text | Select-Object -Skip 1)) { Write-Host $line }
    Write-Host "Evidence: $commitDirectory"
}
exit $exitCode
