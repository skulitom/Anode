#requires -Version 5.1
<#
.SYNOPSIS
    Removes the Anode installation this script belongs to, as installed by install.ps1.
.DESCRIPTION
    Windows Settings runs this from Installed apps. It removes the files install.ps1 copied, its
    PATH entry, Start menu shortcut and Installed apps entry, and unregisters Codex and Claude Code
    servers that run this installation's anode.exe, with their unmodified anode-desktop skill.
    Files you added to the folder, logs in %LOCALAPPDATA%\Anode and machine setup stay; it ends by
    listing what stays and how to remove each without Anode. It asks before quitting a running Anode
    or undoing machine setup; -Quiet never asks and does neither.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\Anode\uninstall.ps1"
#>
[CmdletBinding()]
param(
    # Ask nothing: leave a running Anode and machine setup alone. For scripted removal.
    [switch]$Quiet,
    # Also turn child sessions off and restore the Remote Desktop rendering preference, when no Anode runs.
    [switch]$UndoSetup
)
$ErrorActionPreference = 'Stop'
$InstallDirectory = $PSScriptRoot.TrimEnd('\')
$exe = Join-Path $InstallDirectory 'anode.exe'
$markerPath = Join-Path $InstallDirectory '.anode-install.json'
$securityGuide = 'https://github.com/skulitom/Anode/blob/main/docs/SECURITY.md'
$interactive = -not $Quiet -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
function Ask([string]$Question) { $interactive -and ((Read-Host "$Question [y/N]") -match '^\s*y(es)?\s*$') }
# Settings opens a console for this script; keep it open long enough to read the outcome.
function Finish([int]$Code) {
    if ($interactive) { $null = Read-Host "`nPress Enter to close" }
    exit $Code
}
function Get-Running {
    @(Get-CimInstance Win32_Process -Filter "Name = 'anode.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase) })
}
# What stays on this machine once anode.exe is gone, each with how to remove it without Anode. Read after
# removal, so it names only what is really left; a probe that fails is skipped rather than failing the removal.
function Get-Leftovers {
    function Read-Value([string]$Key, [string]$Name) { try { (Get-ItemProperty -LiteralPath $Key -Name $Name -ErrorAction Stop).$Name } catch { $null } }
    $local = $env:LOCALAPPDATA
    if ($local) {
        # Where 'anode rendering --restore' looks: Anode's own folder, then each packaged app's.
        $folders = @(Join-Path $local 'Anode') + @(Get-ChildItem -LiteralPath (Join-Path $local 'Packages') -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\Anode' })
        $rendering = Read-Value 'HKCU:\Software\Microsoft\Terminal Server Client' 'RemoteDesktop_SuppressWhenMinimized'
        foreach ($folder in @($folders | Where-Object { Test-Path -LiteralPath $_ -PathType Container })) {
            $backup = Join-Path $folder 'rdp-rendering-backup.json'
            if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) { }
            elseif ($rendering -eq 2) {
                $saved = try { (Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json).PreviousValue } catch { 'unreadable' }
                "The Remote Desktop rendering preference Anode changed is not restored. Its earlier value, $(if ($null -eq $saved) { 'none' } else { $saved }), " +
                    "is saved in $backup; set it back by hand before deleting that folder: $securityGuide#per-user-background-rendering"
            } else {
                "$backup is out of date: the Remote Desktop rendering preference no longer has the value Anode set, so leave the preference as it is."
            }
            "Logs and saved settings in ${folder}: delete the folder when you no longer need them."
        }
        $profiles = @('AnodeChrome', 'AnodeEdge', 'AnodeAndroidStudio' | ForEach-Object { Join-Path $local $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if ($profiles.Count) {
            "The seat's browser and Android Studio profiles, still signed in to any site or account you signed in to there: " +
                "$($profiles -join ', '). Delete them to sign those out."
        }
    }
    # The copies connect-agents.ps1 made before changing a client's settings, this removal's included.
    $copies = @(try {
        foreach ($config in @((Join-Path $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }) 'config.toml'),
            (Join-Path $(if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { $env:USERPROFILE }) '.claude.json'))) {
            Get-ChildItem -LiteralPath (Split-Path -Parent $config) -Filter ((Split-Path -Leaf $config) + '.anode-backup-*') -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName }
        }
    } catch { })
    if ($copies.Count) {
        "Copies of agent client settings, saved before Anode changed them, which can hold other servers' settings and tokens: " +
            "$($copies -join ', '). Delete them once those clients work as you want."
    }
    $childSessions = $null
    try {
        if (-not ('AnodeUninstall.ChildSessions' -as [type])) {
            Add-Type -Namespace AnodeUninstall -Name ChildSessions -MemberDefinition '[DllImport("wtsapi32.dll")] public static extern bool WTSIsChildSessionsEnabled(out bool enabled);'
        }
        $enabled = $false
        if ([AnodeUninstall.ChildSessions]::WTSIsChildSessionsEnabled([ref]$enabled)) { $childSessions = $enabled }
    } catch { }
    if ($childSessions -ne $false) {
        $state = if ($childSessions) { 'Windows child sessions are on.' } else { "If you ran 'anode setup', Windows child sessions are on." }
        "$state To turn them off, with no Anode running, run the commands in $securityGuide#turning-child-sessions-off-without-anode " +
            'from an administrator PowerShell.'
    }
    $terminalServer = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
    if ((Read-Value $terminalServer 'fDenyTSConnections') -eq 0) {
        "The Remote Desktop host is on, and nothing in Anode turns it off. If it was off before 'anode setup' and nothing else " +
            'needs it, turn it off in Settings > System > Remote Desktop.'
    }
    $tuning = @()
    if ((Read-Value "$terminalServer\WinStations" 'DWMFRAMEINTERVAL') -eq 15) { $tuning += "the 60 fps frame cap (DWMFRAMEINTERVAL, from --fps 60)" }
    if ((Read-Value 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' 'bEnumerateHWBeforeSW') -eq 1) {
        $tuning += 'the hardware graphics setting (bEnumerateHWBeforeSW, from --gpu)'
    }
    if ($tuning.Count) {
        "Remote Desktop settings 'anode setup' can make are set: $($tuning -join ' and '). Unless your organization set them, " +
            "remove them as $securityGuide#what-anode-setup-changes shows."
    }
}
# Another Anode on this machine (a portable, Scoop or plugin copy) shares that log folder, the seat profiles and machine setup.
function Get-OtherAnode {
    $paths = @(Get-Process -Name anode -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { } }) +
        @(Get-Command -Name anode.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    @($paths | Where-Object { $_ -and -not $_.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase) })[0]
}

try {
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "This folder is not an Anode installation made by install.ps1: $InstallDirectory. Nothing was removed."
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.product -ne 'Anode') { throw 'Invalid Anode installation marker. Nothing was removed.' }
    # A process cannot remove the folder it is working in.
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    Write-Host "Uninstalling Anode $($marker.version) from $InstallDirectory"

    # Nothing may run from this folder: Windows keeps running executables locked.
    $running = Get-Running
    if (@($running | Where-Object { $_.CommandLine -match '\sup(\s|$)' }).Count -and
        (Ask 'Anode is running from this installation. Quit it now? This closes every program in its seat, including unsaved work.')) {
        & $exe quit | Out-Host
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (($running = Get-Running).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    if ($running.Count) {
        $agents = @($running | Where-Object { $_.CommandLine -match '\smcp(\s|$)' }).Count
        throw ('Anode is still running from this folder' + $(if ($agents) { ", including $agents agent session(s) using its MCP server" }) +
            ". Run 'anode quit' (it closes every program in the seat) and close agent sessions that use Anode, then uninstall again. Nothing was removed.")
    }
    $undo = $UndoSetup -or (Ask 'Also turn off Windows child sessions and restore the Remote Desktop rendering preference that Anode changed? This signs out any seat and asks for administrator approval.')

    # Registrations that would otherwise point agents at a missing executable.
    $connector = Join-Path $InstallDirectory 'connect-agents.ps1'
    if (Test-Path -LiteralPath $connector -PathType Leaf) {
        try { & $connector -Remove -Anode $exe }
        catch {
            Write-Warning ("Could not check Codex and Claude Code registrations: $($_.Exception.Message) If they run $exe, remove them " +
                "with 'codex mcp remove anode' or 'claude mcp remove --scope user anode'.")
        }
    }

    # Machine setup is shared by every Anode on this account, and undoing it signs out any seat.
    if ($undo) {
        if (@(Get-Process -Name anode -ErrorAction SilentlyContinue).Count) {
            $undo = $false
            Write-Warning 'Another Anode is running, so machine setup was left on; undoing it would sign out that seat.'
        } else {
            & $exe rendering --restore | Out-Host
            if ($LASTEXITCODE -ne 0) { Write-Warning 'The Remote Desktop rendering preference was not restored; see the message above.' }
            & $exe setup --undo | Out-Host
            if ($LASTEXITCODE -ne 0) { $undo = $false; Write-Warning 'Child sessions were not turned off; see the messages above.' }
        }
    }

    # The files install.ps1 copied. Anything else in the folder was added by someone else and stays.
    $root = $InstallDirectory + '\'
    $ours = @($marker.files | Where-Object { $_ -and $_ -notin @('uninstall.ps1', '.anode-install.json') })
    $failed = @()
    foreach ($relative in $ours) {
        $path = [IO.Path]::GetFullPath((Join-Path $InstallDirectory $relative))
        if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try { Remove-Item -LiteralPath $path -Force } catch { $failed += $relative }
    }
    if ($failed.Count) {
        throw "Could not remove $($failed.Count) file(s), such as $($failed[0]). Close programs using them and uninstall again; Anode stays in Installed apps until then."
    }
    # The folders those files lived in, deepest first, once empty.
    $folders = @{}
    foreach ($relative in $ours) {
        for ($folder = Split-Path -Parent $relative; $folder; $folder = Split-Path -Parent $folder) { $folders[$folder] = $true }
    }
    foreach ($folder in @($folders.Keys | Sort-Object { $_.Split('\').Count } -Descending)) {
        $path = Join-Path $InstallDirectory $folder
        if ((Test-Path -LiteralPath $path -PathType Container) -and -not @(Get-ChildItem -LiteralPath $path -Force).Count) { Remove-Item -LiteralPath $path }
    }
    Write-Host "Removed $($ours.Count) installed files."

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    $kept = @($entries | Where-Object { [Environment]::ExpandEnvironmentVariables($_).Trim('"').TrimEnd('\') -ine $InstallDirectory })
    if ($kept.Count -ne $entries.Count) {
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
        Write-Host 'Removed its folder from your user PATH.'
    }

    # Only a shortcut and an entry that still belong to this installation.
    $shortcut = if ($marker.shortcut) { [string]$marker.shortcut } else { Join-Path ([Environment]::GetFolderPath('Programs')) 'Anode.lnk' }
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        $shell = New-Object -ComObject WScript.Shell
        try { $target = $shell.CreateShortcut($shortcut).TargetPath } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
        if ($target -ieq $exe) {
            Remove-Item -LiteralPath $shortcut
            Write-Host 'Removed the Start menu shortcut.'
        }
    }
    $registration = if ($marker.registration) { [string]$marker.registration } else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Anode' }
    if ((Test-Path -LiteralPath $registration) -and (Get-ItemProperty -LiteralPath $registration).InstallLocation -ieq $InstallDirectory) {
        Remove-Item -LiteralPath $registration -Recurse
        Write-Host 'Removed Anode from Installed apps.'
    }

    foreach ($own in @('uninstall.ps1', '.anode-install.json')) {
        $path = Join-Path $InstallDirectory $own
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $left = @(Get-ChildItem -LiteralPath $InstallDirectory -Force)
    if ($left.Count) { Write-Host "Kept $($left.Count) item(s) you added in $InstallDirectory." }
    else { Remove-Item -LiteralPath $InstallDirectory }

    Write-Host "`nAnode is uninstalled." -ForegroundColor Green
    $leftovers = @(try { Get-Leftovers } catch { "Could not check what stays on this machine ($($_.Exception.Message)); see $securityGuide." })
    if ($leftovers.Count) {
        $other = try { Get-OtherAnode } catch { $null }
        Write-Host $(if ($other) { "These stay on this machine and are shared with another Anode, ${other}: keep them while you use it. Without Anode, each goes like this:" }
            else { 'These stay on this machine. anode.exe is gone, so each says how to remove it without Anode:' })
        foreach ($line in $leftovers) { Write-Host "- $line" }
    }
    Finish 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Finish 1
}
