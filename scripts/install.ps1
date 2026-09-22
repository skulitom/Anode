#requires -Version 5.1
<#
.SYNOPSIS
    Installs or updates Anode for the current Windows user, without elevation, and adds it to
    the Start menu so Windows Search finds it and to Installed apps so Windows can uninstall it.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
    .\install.ps1 -Version 0.3.1 -Client Auto
.EXAMPLE
    .\install.ps1 -PackagePath .\anode-windows-x64.zip -ChecksumPath .\SHA256SUMS -NoPath -NoShortcut
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(latest|v?\d+\.\d+\.\d+)$')][string]$Version = 'latest',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Anode'),
    [ValidateSet('None','Auto','Both','Codex','Claude')][string]$Client = 'None',
    [string]$PackagePath,
    [string]$ChecksumPath,
    [switch]$NoPath,
    [switch]$NoShortcut,
    # Where the Anode shortcut goes; tests point this at a scratch folder.
    [string]$StartMenuDirectory = [Environment]::GetFolderPath('Programs'),
    # The per-user Installed apps entry; tests point this at a scratch key, and '' skips it.
    [string]$RegistrationKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Anode'
)
$ErrorActionPreference = 'Stop'
# Agent runners can omit OS from their environment. Ask the runtime about the host instead.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Anode requires Windows.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Anode requires 64-bit Windows.' }
if ([bool]$PackagePath -ne [bool]$ChecksumPath) { throw 'Supply both -PackagePath and -ChecksumPath for an offline install.' }
if ($InstallDirectory.Contains(';')) { throw 'The installation path must not contain a semicolon (PATH separator).' }
$InstallDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDirectory).TrimEnd('\')
if ($InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory).TrimEnd('\')) { throw 'Choose a dedicated installation folder, not a drive root.' }

# A process started by a packaged app, such as an agent's desktop app, can see a private copy of
# AppData: Windows stores the files it creates there in that app's own storage, where Explorer,
# Windows Search and other terminals never look. Name the app when that happens.
function Get-PrivateOwner([string]$Path) {
    if (-not ('AnodeInstall.Files' -as [type])) {
        Add-Type -Namespace AnodeInstall -Name Files -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);'
    }
    # Best effort: an unreadable file must not fail an installation that has already succeeded.
    try { $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete') } catch { return $null }
    try {
        $stored = New-Object Text.StringBuilder 32768
        if ([AnodeInstall.Files]::GetFinalPathNameByHandleW($stream.SafeFileHandle, $stored, 32768, 0) -eq 0) { return $null }
    } finally { $stream.Dispose() }
    if ($stored.ToString() -match '\\AppData\\Local\\Packages\\([^\\]+)\\LocalCache\\') { return $Matches[1] }
    return $null
}

$markerPath = Join-Path $InstallDirectory '.anode-install.json'
$marker = $null
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
        # Windows PowerShell redraws progress for every chunk, which slows large downloads many times over.
        $savedProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            # Resolve both assets from the same release; latest may change during a download.
            foreach ($name in @('anode-windows-x64.zip','SHA256SUMS')) {
                $asset = @($release.assets | Where-Object { $_.name -eq $name })
                if ($asset.Count -ne 1) { throw "Release $($release.tag_name) is missing $name." }
                $url = [Uri]$asset[0].browser_download_url
                if ($url.Scheme -ne 'https' -or $url.Host -ne 'github.com' -or -not $url.AbsolutePath.StartsWith('/skulitom/Anode/releases/download/')) {
                    throw 'Unexpected release asset URL.'
                }
                $bytes = [double]$asset[0].size
                $size = if ($bytes -ge 1MB) { '{0:0} MB' -f ($bytes / 1MB) } else { '{0:0} KB' -f [math]::Max(1, $bytes / 1KB) }
                Write-Host "Downloading $($release.tag_name)/$name ($size)"
                Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $scratch $name) -TimeoutSec 300
            }
        } finally { $ProgressPreference = $savedProgress }
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
    # uninstall.ps1 reads the marker to remove exactly what this installer added.
    $shortcutPath = if ($NoShortcut -or -not $StartMenuDirectory) { $null } else { Join-Path $StartMenuDirectory 'Anode.lnk' }
    # Older releases have no uninstaller for Installed apps to run.
    $registration = if ($RegistrationKey -and (Test-Path -LiteralPath (Join-Path $payload 'uninstall.ps1') -PathType Leaf)) { $RegistrationKey } else { $null }
    $installMarker = @{ product = 'Anode'; version = $versionOutput.Substring(6); files = $files; shortcut = $shortcutPath; registration = $registration }
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
    $privateOwner = Get-PrivateOwner (Join-Path $InstallDirectory 'anode.exe')
    if ($privateOwner) {
        Write-Warning "Windows kept this installation private to $privateOwner, the app this installer runs inside; your own terminals and the Start menu cannot see it. Run install.ps1 from your own terminal instead."
    }
    if (-not $NoPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $entries = @($userPath -split ';' | Where-Object { $_ })
        if (-not @($entries | Where-Object { [Environment]::ExpandEnvironmentVariables($_).Trim('"').TrimEnd('\') -ieq $InstallDirectory }).Count) {
            [Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDirectory) -join ';'), 'User')
            Write-Host 'Added to your user PATH. Open a new terminal to use anode by name.'
        }
        # This also makes anode available immediately when invoked in this PowerShell process.
        if (-not @($env:Path -split ';' | Where-Object { $_.Trim('"').TrimEnd('\') -ieq $InstallDirectory }).Count) {
            $env:Path += ';' + $InstallDirectory
        }
    }
    if (-not $NoShortcut) {
        # Windows Search lists Start menu shortcuts as apps. Opening this one shows the viewer,
        # starting Anode first when needed. A failure here leaves the installation usable.
        try {
            if (-not $StartMenuDirectory) { throw 'Windows reported no Start menu folder for this account.' }
            $null = New-Item -ItemType Directory -Path $StartMenuDirectory -Force
            $shortcut = Join-Path $StartMenuDirectory 'Anode.lnk'
            $existed = Test-Path -LiteralPath $shortcut
            $shell = New-Object -ComObject WScript.Shell
            try {
                $link = $shell.CreateShortcut($shortcut)
                $link.TargetPath = Join-Path $InstallDirectory 'anode.exe'
                $link.WorkingDirectory = $InstallDirectory
                $link.Description = 'Open the Anode background desktop'
                $link.Save()
            } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
            $shortcutOwner = Get-PrivateOwner $shortcut
            if ($shortcutOwner) {
                Write-Warning "Windows kept the Start menu shortcut private to $shortcutOwner, the app this installer runs inside, so Windows Search cannot list Anode. Run install.ps1 again from your own terminal to add it."
            } elseif (-not $existed) { Write-Host 'Added Anode to the Start menu. Search for Anode to open its viewer.' }
        } catch {
            Write-Warning "Anode is installed, but its Start menu shortcut could not be created: $($_.Exception.Message)"
        }
    }
    $newVersion = $versionOutput.Substring(6)
    $previous = if ($marker) { [string]$marker.version } else { $null }
    if ($registration) {
        # Settings > Apps > Installed apps lists per-user entries without administrator rights;
        # its Uninstall button runs uninstall.ps1 from this folder.
        try {
            $registered = Test-Path -LiteralPath $registration
            if (-not $registered) { $null = New-Item -Path $registration -Force }
            $uninstall = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDirectory 'uninstall.ps1') + '"'
            $strings = [ordered]@{
                DisplayName = 'Anode'; DisplayVersion = $newVersion; Publisher = 'skulitom'
                DisplayIcon = (Join-Path $InstallDirectory 'anode.exe') + ',0'; InstallLocation = $InstallDirectory
                InstallDate = (Get-Date -Format 'yyyyMMdd'); UninstallString = $uninstall; QuietUninstallString = "$uninstall -Quiet"
                URLInfoAbout = 'https://github.com/skulitom/Anode'; HelpLink = 'https://github.com/skulitom/Anode/blob/main/docs/INSTALL.md#remove'
                Comments = 'Background Windows desktop for AI agents'
            }
            foreach ($name in $strings.Keys) { $null = New-ItemProperty -LiteralPath $registration -Name $name -Value $strings[$name] -PropertyType String -Force }
            $kilobytes = [int][Math]::Ceiling((Get-ChildItem -LiteralPath $InstallDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
            foreach ($number in @(@('EstimatedSize', $kilobytes), @('NoModify', 1), @('NoRepair', 1))) {
                $null = New-ItemProperty -LiteralPath $registration -Name $number[0] -Value $number[1] -PropertyType DWord -Force
            }
            if (-not $registered) { Write-Host 'Added Anode to Installed apps in Windows Settings, where you can uninstall it.' }
        } catch {
            Write-Warning "Anode is installed, but it could not be added to Installed apps: $($_.Exception.Message)"
        }
    }
    if ($previous -and $previous -ne $newVersion) { Write-Host "Updated Anode $previous -> $newVersion in $InstallDirectory" -ForegroundColor Green }
    else { Write-Host "Installed $versionOutput in $InstallDirectory" -ForegroundColor Green }
    if ($Client -ne 'None') {
        & (Join-Path $InstallDirectory 'connect-agents.ps1') -Client $Client -Anode (Join-Path $InstallDirectory 'anode.exe')
    }
    $anodeCommand = if ($NoPath) { "& '" + (Join-Path $InstallDirectory 'anode.exe').Replace("'", "''") + "'" } else { 'anode' }
    if ($previous) {
        Write-Host 'Reopen agent sessions that use Anode so they load the installed version.'
    } else {
        $steps = @(
            @('doctor', 'checks prerequisites; changes nothing'),
            @('setup', 'one administrator prompt'))
        if ($Client -eq 'None') { $steps += , @('configure', 'registers Codex/Claude Code; then start a new agent session') }
        $steps += , @('start --hidden', 'starts the background desktop')
        $width = ($steps | ForEach-Object { "$anodeCommand $($_[0]) | Out-Host".Length } | Measure-Object -Maximum).Maximum
        Write-Host $(if ($NoPath) { 'Next:' } else { 'Next, in a new terminal:' })
        foreach ($step in $steps) { Write-Host ('  ' + "$anodeCommand $($step[0]) | Out-Host".PadRight($width) + '  # ' + $step[1]) }
    }
    Write-Host 'Guide: https://github.com/skulitom/Anode#set-up-and-connect'
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $keepScratch -and $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedScratch) -like 'anode-install-*') {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
}
