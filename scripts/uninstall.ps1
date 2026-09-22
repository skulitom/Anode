#requires -Version 5.1
<#
.SYNOPSIS
    Removes the Anode installation this script belongs to, as installed by install.ps1.
.DESCRIPTION
    Windows Settings runs this from Installed apps. It removes the files install.ps1 copied, its
    PATH entry, Start menu shortcut and Installed apps entry, and unregisters Codex and Claude Code
    servers that run this installation's anode.exe, with their unmodified anode-desktop skill.
    Files you added to the folder, logs in %LOCALAPPDATA%\Anode and machine setup stay. It asks
    before quitting a running Anode or undoing machine setup; -Quiet never asks and does neither.
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
$setupGuide = 'https://github.com/skulitom/Anode/blob/main/docs/SECURITY.md#what-anode-setup-changes'
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
    Write-Host "Logs and saved settings remain in $(Join-Path $env:LOCALAPPDATA 'Anode'); delete that folder if you no longer need them."
    if (-not $undo) { Write-Host "If you ran 'anode setup', child sessions stay on; to turn them off, see $setupGuide" }
    Finish 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Finish 1
}
