#requires -Version 5.1
<#
.SYNOPSIS
    Installs or updates Anode for the current Windows user, without elevation.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
    .\install.ps1 -Version 0.3.1 -Client Auto
.EXAMPLE
    .\install.ps1 -PackagePath .\anode-windows-x64.zip -ChecksumPath .\SHA256SUMS -NoPath
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(latest|v?\d+\.\d+\.\d+)$')][string]$Version = 'latest',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Anode'),
    [ValidateSet('None','Auto','Both','Codex','Claude')][string]$Client = 'None',
    [string]$PackagePath,
    [string]$ChecksumPath,
    [switch]$NoPath
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Anode requires Windows.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Anode requires 64-bit Windows.' }
if ([bool]$PackagePath -ne [bool]$ChecksumPath) { throw 'Supply both -PackagePath and -ChecksumPath for an offline install.' }
if ($InstallDirectory.Contains(';')) { throw 'The installation path must not contain a semicolon (PATH separator).' }
$InstallDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDirectory).TrimEnd('\')
if ($InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory).TrimEnd('\')) { throw 'Choose a dedicated installation folder, not a drive root.' }
$markerPath = Join-Path $InstallDirectory '.anode-install.json'
if ((Test-Path -LiteralPath $InstallDirectory) -and @(Get-ChildItem -LiteralPath $InstallDirectory -Force).Count -gt 0) {
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Folder is not an installer-managed Anode installation: $InstallDirectory. Choose an empty folder."
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.product -ne 'Anode') { throw 'Invalid Anode installation marker.' }
}

# All downloads, extraction and rollback copies live in this invocation's private folder.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('anode-install-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$release = $null
$keepScratch = $false
try {
    if (-not $PackagePath) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $endpoint = if ($Version -eq 'latest') { 'latest' } else { 'tags/v' + $Version.TrimStart('v') }
        try {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/skulitom/Anode/releases/$endpoint" -Headers @{ 'User-Agent' = 'Anode-installer' } -TimeoutSec 60
        } catch { throw "Cannot find the Anode release ($Version). Check your connection or download from https://github.com/skulitom/Anode/releases. $($_.Exception.Message)" }
        # Resolve both assets from the same release; latest may change during a download.
        foreach ($name in @('anode-windows-x64.zip','SHA256SUMS')) {
            $asset = @($release.assets | Where-Object { $_.name -eq $name })
            if ($asset.Count -ne 1) { throw "Release $($release.tag_name) is missing $name." }
            $url = [Uri]$asset[0].browser_download_url
            if ($url.Scheme -ne 'https' -or $url.Host -ne 'github.com' -or -not $url.AbsolutePath.StartsWith('/skulitom/Anode/releases/download/')) {
                throw 'Unexpected release asset URL.'
            }
            Write-Host "Downloading $($release.tag_name)/$name"
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $scratch $name) -TimeoutSec 300
        }
        $PackagePath = Join-Path $scratch 'anode-windows-x64.zip'
        $ChecksumPath = Join-Path $scratch 'SHA256SUMS'
    }
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
    $ChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path
    $packageName = [IO.Path]::GetFileName($PackagePath)
    $pattern = '^([a-fA-F0-9]{64})\s+\*?' + [regex]::Escape($packageName) + '$'
    $hashes = @(Get-Content -LiteralPath $ChecksumPath | ForEach-Object {
        if ($_ -match $pattern) { $Matches[1] }
    })
    if ($hashes.Count -ne 1) { throw "Expected exactly one checksum for $packageName in SHA256SUMS." }
    if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ne $hashes[0]) { throw 'SHA-256 mismatch. Nothing installed. Download the release again.' }
    Write-Host 'SHA-256 verified.'

    $payload = Join-Path $scratch 'payload'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Reject traversal, alternate streams and ambiguous Windows paths before extraction.
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
                @($name.Split('\') | Where-Object { $_ -eq '..' -or $_ -match '[. ]$' }).Count -gt 0) {
                throw "Unsafe archive path: $name"
            }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $payload)
    foreach ($required in @('anode.exe','connect-agents.ps1','install.ps1','README.md','LICENSE','docs\INSTALL.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Incomplete release archive: missing $required." }
    }
    $exe = Join-Path $payload 'anode.exe'
    $versionOutput = (& $exe version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^anode \d+\.\d+\.\d+$') { throw 'The downloaded executable could not run. Check Windows compatibility or security software.' }
    if ($release -and $release.tag_name -ne ('v' + $versionOutput.Substring(6))) { throw 'Executable version does not match the release tag.' }

    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object { $_.FullName.Substring($payload.Length + 1) })
    $installMarker = @{ product = 'Anode'; version = $versionOutput.Substring(6); files = $files }
    $installMarker | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload '.anode-install.json') -Encoding UTF8
    $files += '.anode-install.json'
    # Check destinations before writing, including locked running executables and junctions.
    foreach ($relative in $files) {
        $destination = Join-Path $InstallDirectory $relative
        $ancestor = $destination
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                $item = Get-Item -LiteralPath $ancestor -Force
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Installation through a junction/symlink is unsupported: $ancestor" }
            }
            $ancestor = Split-Path -Parent $ancestor
        }
        if (Test-Path -LiteralPath $destination) {
            # Process exit and antivirus scanning can briefly retain a file handle.
            # A running instance still fails within five seconds, before any copies.
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while ($true) {
                try {
                    $handle = [IO.File]::Open($destination, 'Open', 'ReadWrite', 'None')
                    $handle.Dispose()
                    break
                } catch {
                    if ([DateTime]::UtcNow -ge $deadline) {
                        throw "Cannot replace $destination. Close Anode and connected MCP clients before updating. No files changed. $($_.Exception.Message)"
                    }
                    Start-Sleep -Milliseconds 200
                }
            }
        }
    }
    $backup = Join-Path $scratch 'backup'
    $written = [Collections.Generic.List[string]]::new()
    try {
        foreach ($relative in $files) {
            $destination = Join-Path $InstallDirectory $relative
            $saved = Join-Path $backup $relative
            if (Test-Path -LiteralPath $destination) {
                $null = New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force
                Copy-Item -LiteralPath $destination -Destination $saved
            }
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
            $written.Add($relative)
            Copy-Item -LiteralPath (Join-Path $payload $relative) -Destination $destination -Force
        }
    } catch {
        $installError = $_
        foreach ($relative in $written) {
            $destination = Join-Path $InstallDirectory $relative
            $saved = Join-Path $backup $relative
            try {
                if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $destination -Force }
                elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination -Force }
            } catch { $keepScratch = $true }
        }
        if ($keepScratch) { throw "Update failed and rollback was incomplete. Original files remain in $backup. $($installError.Exception.Message)" }
        throw $installError
    }
    if (-not $NoPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { $_ })
        if (-not @($entries | Where-Object { [Environment]::ExpandEnvironmentVariables($_).Trim('"').TrimEnd('\') -ieq $InstallDirectory }).Count) {
            [Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDirectory) -join ';'), 'User')
        }
        # This also makes anode available immediately when invoked in this PowerShell process.
        if (-not @($env:Path -split ';' | Where-Object { $_.Trim('"').TrimEnd('\') -ieq $InstallDirectory }).Count) {
            $env:Path += ';' + $InstallDirectory
        }
        Write-Host 'Added to your user PATH. Open a new terminal to use anode by name.'
    }
    Write-Host "Installed $versionOutput in $InstallDirectory" -ForegroundColor Green
    if ($Client -ne 'None') {
        & (Join-Path $InstallDirectory 'connect-agents.ps1') -Client $Client -Anode (Join-Path $InstallDirectory 'anode.exe')
    }
    Write-Host "Next: & '$InstallDirectory\anode.exe' doctor | Out-Host"
    Write-Host 'Then: anode setup (one UAC prompt), anode configure, anode start --hidden.'
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $keepScratch -and $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedScratch) -like 'anode-install-*') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
