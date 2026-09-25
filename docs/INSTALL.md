# Install, update and remove Anode

## Requirements

- Windows 10/11 Pro, Enterprise or Education, or Windows Server with the Remote Desktop host.
  Windows Home cannot host a child session.
- The downloadable package targets Windows x64. ARM64 is not validated.
- PowerShell 5.1 or later for the installer and agent connector.
- Administrator permission for the separate, one-time `anode setup` step.
- No .NET installation for release builds. Building from source requires the .NET 10 SDK.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) only if you want a virtual gamepad, and
  [HidHide](https://github.com/nefarius/HidHide/releases) to keep it inside the seat while you use your
  own games. Both need an administrator once; HidHide asks for a restart.

## Recommended: install for your user

Download [install.ps1](https://github.com/skulitom/Anode/releases/latest/download/install.ps1),
inspect it if desired, and run from its folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

It downloads the latest release and its `SHA256SUMS` from GitHub, verifies the archive, runs
`anode version` to check the executable, installs in `%LOCALAPPDATA%\Programs\Anode`, appends
that folder to your **user** PATH and adds an **Anode** shortcut to your Start menu, so searching
for Anode from the taskbar finds it. It also lists Anode in **Settings → Apps → Installed apps**,
where you can [uninstall](#remove) it. It uses no administrator prompt. Open a new terminal afterward;
restart your terminal application or sign out and in if it retains an old PATH.
You can always use the full path immediately:

```powershell
& "$env:LOCALAPPDATA\Programs\Anode\anode.exe" doctor | Out-Host
```

Useful options:

```powershell
# Choose a version and register installed Codex/Claude Code CLIs as well.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Version 0.9.0 -Client Auto
# A dedicated custom folder; leave PATH and the Start menu alone.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -InstallDirectory C:\Tools\Anode -NoPath -NoShortcut
```

Opening **Anode** from the Start menu, or double-clicking `anode.exe`, shows the viewer. When Anode
is not running, it starts Anode first. When the one-time [machine setup](#one-time-machine-setup)
is missing, it offers to run it, with the usual administrator prompt.

An agent inside a packaged desktop app, such as Claude or Codex for Windows, can see a private copy
of AppData: Windows keeps files it creates there private to that app, out of the Start menu and
your own terminals. The installer warns when that happens to the shortcut or the installation.
Run it once from your own terminal to fix it.

`-Client` accepts `None` (default), `Auto`, `Codex`, `Claude` or `Both`. Client registration failures
leave the installation in place; fix the reported client problem and rerun `anode configure`.
Use an empty folder for the first installation. The installer updates only folders bearing its
`.anode-install.json` marker and preserves unrelated files during updates.

## Portable or offline

Download [anode-windows-x64.zip](https://github.com/skulitom/Anode/releases/latest/download/anode-windows-x64.zip)
and [SHA256SUMS](https://github.com/skulitom/Anode/releases/latest/download/SHA256SUMS).
Compare `Get-FileHash .\anode-windows-x64.zip -Algorithm SHA256` with the ZIP entry in `SHA256SUMS`,
then extract the whole archive. Keep `connect-agents.ps1` next to `anode.exe` for `configure`.
The core CLI/MCP runtime can also run as a standalone executable.

To install from already downloaded files without network access:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -PackagePath .\anode-windows-x64.zip -ChecksumPath .\SHA256SUMS
```

Binaries and scripts are currently **unsigned**. SmartScreen or an organization's application
policy may block them. Checksums detect a corrupt or mismatched download, not publisher identity.
Get the files from this repository's releases and follow your organization's policy.

## Other ways to install

**Scoop.** This repository is also a Scoop bucket:

```powershell
scoop bucket add anode https://github.com/skulitom/Anode
scoop install anode/anode
```

Scoop puts `anode` on your PATH. Continue with the [one-time machine setup](#one-time-machine-setup)
and [agent configuration](#configure-your-agent) below. Scoop refuses to update while Anode is
running, so run `anode quit | Out-Host` and close MCP clients using Anode before
`scoop update anode`.

**Claude Code plugin.** The plugin registers the MCP server and the `anode-desktop` skill in
Claude Code; it does not install `anode.exe`. Install Anode first (above or Scoop) so `anode` is on
your PATH, run the machine setup, then follow
[the plugin instructions](CONNECTING-AGENTS.md#1-claude-code). Use the plugin or
`anode configure claude`, not both.

## One-time machine setup

```powershell
anode doctor | Out-Host
anode setup | Out-Host
anode doctor | Out-Host
```

A nonzero `doctor` result before setup means prerequisites are missing. Follow the reported fix;
it is not a failed installation. `setup` enables Remote Desktop and child sessions, asks for UAC
approval and may restart Remote Desktop Services. It adds no firewall rules. See
[the exact machine changes and undo behavior](SECURITY.md#what-anode-setup-changes).

For GPU rendering and a higher remote-session frame cap, opt in with
`anode setup --fps 60 --gpu`, then reboot if requested.

## Configure your agent

```powershell
anode configure | Out-Host
anode configure codex | Out-Host   # or choose one client
anode configure claude | Out-Host
```

The command detects CLIs on PATH, uses their registration commands, keeps timestamped backups
beside existing settings, configures a 420-second tool timeout and installs the `anode-desktop`
skill. The argument is `auto` (default), `codex`, `claude` or `both`; `both` checks that both
CLIs exist before changing either, and `auto` with neither installed prints guidance. Add
`--no-skill` for MCP registration alone. Customized skills are preserved
([skill locations](FOR-AGENTS.md#skill-installation-and-control)). No approval policies change
and no seat starts. Restart the agent to reload tools. For Claude Desktop, see
[Claude Desktop](CONNECTING-AGENTS.md#claude-desktop); for manual configuration, VS Code and
Cursor, see [Connecting agents](CONNECTING-AGENTS.md).

## Verify the installation

```powershell
anode version | Out-Host
anode selftest --quick | Out-Host
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

The first two commands do not start a seat. `start` creates the background session;
`capabilities` checks actual capture and known input blockers. If sign-in fails after Windows
Hello/PIN login, open the viewer from the tray icon and click **Sign in…** in the header to request
Windows' credential dialog. For a new daemon, use `anode start --sign-in`.
See [Troubleshooting](TROUBLESHOOTING.md) for readiness, capture and input problems.

## Update

Finish work in the seat, then stop Anode and close agent sessions that use its MCP server:

```powershell
anode quit | Out-Host  # closes every application in the seat; save work first
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Repeat your `-InstallDirectory` if you used a custom folder. The installer refuses locked files,
verifies the new package before copying, and rolls back file copies if replacement fails.
The stable executable path keeps client registrations working. Reopen the agent afterward.
Use `-Version` to choose a previous release with the same package format.
Portable users should extract a fresh folder, then run its `anode configure` to update paths.
Scoop users run `anode quit | Out-Host`, close MCP clients using Anode, then `scoop update anode`.

## Remove

For an installation made by `install.ps1`, open **Settings → Apps → Installed apps**, find **Anode**
and choose **Uninstall**. Or run the uninstaller from the installation folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\Anode\uninstall.ps1"
```

It offers to quit a running Anode, which closes every program in its seat, and stops if agent
sessions still use the installation. It removes the files the installer copied (files you added to
the folder stay), the PATH entry, Start menu shortcut and Installed apps entry, and Codex and Claude
Code registrations that run this installation's `anode.exe`, with their unmodified `anode-desktop`
skill. It asks before undoing machine setup: turning child sessions off and restoring the RDP
rendering value, with one administrator prompt, and only while no Anode runs, because turning child
sessions off signs out any seat. `-UndoSetup` does that without asking; `-Quiet` asks nothing and
leaves a running Anode and machine setup alone. Remote Desktop stays enabled, and logs remain in
`%LOCALAPPDATA%\Anode`.

For portable, Scoop and plugin installs, or to remove Anode by hand:

1. Save work, run `anode quit`, and close MCP clients using Anode.
2. Remove registrations with `codex mcp remove anode` and/or
   `claude mcp remove --scope user anode`, or `/plugin uninstall anode@anode` in Claude Code if
   you used the plugin. For other clients, remove only their Anode entry.
   Remove the `anode-desktop` skill folder from the [client skill locations](FOR-AGENTS.md#skill-installation-and-control)
   if installed; MCP registration and skills are independent.
3. Optionally run `anode rendering --restore` to restore the saved per-user RDP rendering value,
   and `anode setup --undo` to disable child sessions. Remote Desktop remains enabled;
   [security documentation](SECURITY.md) explains why and how to disable it separately.
   If you ran `setup` with `--fps 60` or `--gpu`, also remove the values they set
   ([how](SECURITY.md#what-anode-setup-changes)).
4. Delete the installation folder you chose, remove that exact entry from your user PATH in **Edit
   environment variables for your account**, and delete `Anode.lnk` from
   `%APPDATA%\Microsoft\Windows\Start Menu\Programs` if present.
   Scoop users run `scoop uninstall anode` instead.
5. Logs and saved rendering state remain in `%LOCALAPPDATA%\Anode`. Keep them for troubleshooting
   or delete them after restoring settings. Client config backups remain beside the originals.

## Build from source

```powershell
git clone https://github.com/skulitom/Anode.git
cd Anode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest -Package
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install.ps1 -PackagePath artifacts\release\anode-windows-x64.zip -ChecksumPath artifacts\release\SHA256SUMS
```

If Anode is already running from `dist\`, add `-OutputDirectory artifacts\pkg-build` to the
build command so it does not overwrite the running executable; the package still lands in
`artifacts\release`. See [Contributing](../CONTRIBUTING.md) for verification and releases.
