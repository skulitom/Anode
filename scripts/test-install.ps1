#requires -Version 5.1
<# Disposable package/install checks. Never changes user PATH or real client configuration. #>
param([string]$ArchiveDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'))
$ErrorActionPreference = 'Stop'
$ArchiveDirectory = (Resolve-Path -LiteralPath $ArchiveDirectory).Path
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('anode-install-check-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $workspace
$installer = Join-Path $PSScriptRoot 'install.ps1'
$package = Join-Path $ArchiveDirectory 'anode-windows-x64.zip'
$checksums = Join-Path $ArchiveDirectory 'SHA256SUMS'
$installation = Join-Path $workspace 'Anode with spaces'
$originalPath = [Environment]::GetEnvironmentVariable('Path', 'User')
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert ($failure -and $failure -match $Pattern) "Expected failure '$Pattern', got '$failure'."
}
try {
    # The archive must install into an arbitrary path without setup or an installed runtime.
    $installOutput = @(& $installer -PackagePath $package -ChecksumPath $checksums -InstallDirectory $installation -NoPath 6>&1 | ForEach-Object { "$_" }) -join "`n"
    Write-Host $installOutput
    $exe = Join-Path $installation 'anode.exe'
    $quotedExe = "& '" + $exe.Replace("'", "''") + "'"
    Assert ($installOutput.Contains("$quotedExe doctor | Out-Host") -and $installOutput.Contains("$quotedExe configure | Out-Host")) 'Next steps must use the installed path without PATH.'
    Assert (-not $installOutput.Contains('Added to your user PATH') -and $installOutput.Contains('#set-up-and-connect')) 'Installer closing text is wrong for -NoPath.'
    $version = (& $exe version | Out-String).Trim()
    Assert ($LASTEXITCODE -eq 0 -and $version -match '^anode \d+\.\d+\.\d+$') 'Installed executable failed.'
    $guide = (& $exe guide --json | Out-String) | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 0 -and $guide.name -eq 'anode' -and $guide.guide.Length -gt 100) 'Standalone guide unavailable.'
    Assert (Test-Path -LiteralPath (Join-Path $installation 'skills\anode-desktop\SKILL.md')) 'Packaged skill is missing.'
    & $exe configure --help | Out-Host
    Assert ($LASTEXITCODE -eq 0) 'Configure help failed.'
    $ErrorActionPreference = 'Continue'
    & $exe configure invalid-client 2>$null | Out-Host
    $ErrorActionPreference = 'Stop'
    Assert ($LASTEXITCODE -eq 2) 'Invalid client must fail before configuration.'
    # Exercise the real CLI launcher with a recording sidecar, never a real agent CLI.
    $sidecar = Join-Path $installation 'connect-agents.ps1'
    $sidecarText = [IO.File]::ReadAllText($sidecar)
    try {
        [IO.File]::WriteAllText($sidecar, @'
param([string]$Client, [string]$Anode, [switch]$NoSkill)
@{ client = $Client; anode = $Anode; noSkill = [bool]$NoSkill } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'configure-call.json')
Write-Output 'connector-output-ok'
'@)
        $configureOutput = (& $exe configure codex | Out-String)
        Assert ($LASTEXITCODE -eq 0) 'Configure launcher failed.'
        Assert ($configureOutput.Contains('connector-output-ok')) 'Configure launcher lost script output.'
        $record = Get-Content -LiteralPath (Join-Path $installation 'configure-call.json') -Raw | ConvertFrom-Json
        Assert ($record.client -eq 'codex' -and $record.anode -eq $exe) 'Configure launcher lost arguments or path with spaces.'
        & $exe configure codex --no-skill | Out-Host
        $record = Get-Content -LiteralPath (Join-Path $installation 'configure-call.json') -Raw | ConvertFrom-Json
        Assert ($LASTEXITCODE -eq 0 -and $record.noSkill) 'Configure lost the skill opt-out.'
        [IO.File]::WriteAllText($sidecar, 'exit 7')
        & $exe configure codex | Out-Host
        Assert ($LASTEXITCODE -eq 7) 'Configure launcher lost script failure code.'
    } finally { [IO.File]::WriteAllText($sidecar, $sidecarText) }
    Write-Host '[ok] install, executable and configure argument validation'

    $sentinel = Join-Path $installation 'user-note.txt'
    Set-Content -LiteralPath $sentinel -Value 'keep this'
    $updateOutput = @(& $installer -PackagePath $package -ChecksumPath $checksums -InstallDirectory $installation -NoPath 6>&1 | ForEach-Object { "$_" }) -join "`n"
    Write-Host $updateOutput
    Assert ($updateOutput.Contains('Reopen agent sessions') -and -not $updateOutput.Contains('Next:')) 'Repeat installation must not repeat first-run steps.'
    Assert ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'keep this') 'Update removed unrelated data.'
    Write-Host '[ok] repeat installation preserves unrelated files'

    $badHash = Join-Path $workspace 'bad-checksums'
    Set-Content -LiteralPath $badHash -Value (('0' * 64) + '  anode-windows-x64.zip')
    $originalHash = (Get-FileHash -LiteralPath $exe).Hash
    Expect-Failure { & $installer -PackagePath $package -ChecksumPath $badHash -InstallDirectory $installation -NoPath } 'SHA-256 mismatch'
    Assert ((Get-FileHash -LiteralPath $exe).Hash -eq $originalHash) 'Hash failure changed installed files.'
    Write-Host '[ok] corrupt download rejected before replacing files'

    $lock = [IO.File]::Open($exe, 'Open', 'Read', 'Read')
    try {
        Expect-Failure { & $installer -PackagePath $package -ChecksumPath $checksums -InstallDirectory $installation -NoPath } 'Cannot replace'
    } finally { $lock.Dispose() }
    Assert ((Get-FileHash -LiteralPath $exe).Hash -eq $originalHash) 'Locked-file failure changed installation.'
    Write-Host '[ok] locked executable refused without changes'

    # Fail after earlier files have been replaced and confirm the prior install is restored.
    $readme = Join-Path $installation 'README.md'
    [IO.File]::WriteAllText($readme, 'previous installed documentation')
    $global:AnodeInstallInjectFailure = $true
    function Copy-Item {
        param([string]$LiteralPath, [string]$Destination, [switch]$Force)
        if ($global:AnodeInstallInjectFailure -and $LiteralPath -like '*\payload\README.md') {
            $global:AnodeInstallInjectFailure = $false
            throw 'Injected file replacement failure'
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    try {
        Expect-Failure { & $installer -PackagePath $package -ChecksumPath $checksums -InstallDirectory $installation -NoPath } 'Injected file replacement failure'
        Assert ([IO.File]::ReadAllText($readme) -eq 'previous installed documentation') 'Rollback did not restore documentation.'
        Assert ((Get-FileHash -LiteralPath $exe).Hash -eq $originalHash) 'Rollback did not restore executable.'
    } finally { Remove-Item -LiteralPath Function:\Copy-Item }
    Write-Host '[ok] failed update rolls back replaced files'

    $unmanaged = Join-Path $workspace 'unmanaged'
    $null = New-Item -ItemType Directory -Path $unmanaged
    Set-Content -LiteralPath (Join-Path $unmanaged 'keep.txt') -Value 'keep'
    Expect-Failure { & $installer -PackagePath $package -ChecksumPath $checksums -InstallDirectory $unmanaged -NoPath } 'not an installer-managed'
    Expect-Failure { & $installer -PackagePath $package -InstallDirectory $installation -NoPath } 'both -PackagePath'
    Write-Host '[ok] existing folders and offline parameters protected'

    # A valid checksum must not permit a path-traversal archive to escape extraction.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unsafeZip = Join-Path $workspace 'unsafe.zip'
    $zip = [IO.Compression.ZipFile]::Open($unsafeZip, 'Create')
    try { $null = $zip.CreateEntry('../escaped.txt') } finally { $zip.Dispose() }
    $unsafeHash = Join-Path $workspace 'unsafe-checksums'
    Set-Content -LiteralPath $unsafeHash -Value ((Get-FileHash -LiteralPath $unsafeZip).Hash + '  unsafe.zip')
    Expect-Failure { & $installer -PackagePath $unsafeZip -ChecksumPath $unsafeHash -InstallDirectory $installation -NoPath } 'Unsafe archive path'
    Write-Host '[ok] archive traversal rejected'

    # Exercise the download path with local assets; no network access.
    # Globals: the mocks run inside install.ps1, whose $Version parameter hides this script's $version.
    $global:AnodeTestAssets = $ArchiveDirectory
    $global:AnodeTestTag = 'v' + $version.Substring(6)
    function Invoke-RestMethod {
        param([string]$Uri, [hashtable]$Headers, [int]$TimeoutSec)
        [pscustomobject]@{ tag_name = $global:AnodeTestTag; assets = @('anode-windows-x64.zip', 'SHA256SUMS' | ForEach-Object {
            [pscustomobject]@{ name = $_; size = (Get-Item -LiteralPath (Join-Path $global:AnodeTestAssets $_)).Length
                browser_download_url = "https://github.com/skulitom/Anode/releases/download/$global:AnodeTestTag/$_" } }) }
    }
    function Invoke-WebRequest {
        param([switch]$UseBasicParsing, [Uri]$Uri, [string]$OutFile, [int]$TimeoutSec)
        if ($ProgressPreference -ne 'SilentlyContinue') { throw 'Download progress was not suppressed.' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $global:AnodeTestAssets ([IO.Path]::GetFileName($Uri.AbsolutePath))) -Destination $OutFile
    }
    try {
        $downloaded = Join-Path $workspace 'downloaded'
        $downloadOutput = @(& $installer -InstallDirectory $downloaded -NoPath 6>&1 | ForEach-Object { "$_" }) -join "`n"
    } finally { Remove-Item -LiteralPath Function:\Invoke-RestMethod, Function:\Invoke-WebRequest }
    Assert ($downloadOutput -match 'Downloading v\d+\.\d+\.\d+/anode-windows-x64\.zip \(\d+ MB\)' -and
        (Test-Path -LiteralPath (Join-Path $downloaded 'anode.exe'))) "Release download path failed: $downloadOutput"
    Write-Host '[ok] release download path without progress redraws'

    # A separate process owns fake client homes and fake CLIs. No real client is invoked.
    $clientRoot = Join-Path $workspace 'clients'
    $null = New-Item -ItemType Directory -Path $clientRoot
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $start.FileName)) { $start.FileName = 'powershell.exe' }
    $script = Join-Path $PSScriptRoot 'test-connect-agents.ps1'
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -Anode "' + $exe + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['CODEX_HOME'] = Join-Path $clientRoot 'codex'
    $start.EnvironmentVariables['CLAUDE_CONFIG_DIR'] = Join-Path $clientRoot 'claude'
    $start.EnvironmentVariables['ANODE_TEST_CLIENT_ROOT'] = $clientRoot
    $start.EnvironmentVariables['USERPROFILE'] = Join-Path $clientRoot 'profile'
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    Write-Host $stdout.GetAwaiter().GetResult()
    Write-Host $stderr.GetAwaiter().GetResult()
    Assert ($process.ExitCode -eq 0) 'Isolated connector checks failed.'
    $process.Dispose()
    Assert ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $originalPath) 'Tests changed user PATH.'
    Write-Host 'All installation and configuration checks passed.' -ForegroundColor Green
} finally {
    $resolved = [IO.Path]::GetFullPath($workspace)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -like 'anode-install-check-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
