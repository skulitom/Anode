## Install Anode on Windows

Download **anode-windows-x64.zip** below for the portable executable, agent connector and docs.
Release builds include .NET; no SDK or runtime installation is needed.

For a per-user installation, download **install.ps1** and run it in PowerShell. It adds Anode to
your PATH, the Start menu and **Settings → Apps → Installed apps**:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Open a new terminal, then:

```powershell
anode doctor | Out-Host
anode setup | Out-Host
anode configure | Out-Host
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

`configure` finds installed Codex and Claude Code CLIs, backs up their settings, and installs
the discoverable `anode-desktop` skill. Use `--no-skill` for MCP registration alone.
Restart the agent afterward. Other MCP clients can launch `anode.exe` with the argument `mcp`.

## What's new in 0.7.0

**A calmer, more modern viewer.** The title bar, header, details and footer form one dark surface:
the title bar is dark and, on Windows 11, matches the header, with no divider lines, separators or
sizing grip. Buttons carry icons and rounded hover, pressed and checked states, Stop seat is always
a tinted button, and the seat state has a colored dot that stays visible at the minimum width.
Until Remote Desktop connects, a dark screen shows the state, its message and progress instead of a
blank window, and it steps aside the moment the connection is up. The tray menu matches Windows 11.
Labels, control names, shortcuts and full-screen safety controls are unchanged.

**Find, open and remove Anode like other apps.** The installer adds Anode to the Start menu, so
Windows Search finds it, and to Installed apps. Opening Anode, or double-clicking `anode.exe`, shows
the viewer, starting Anode or a stopped seat first; when machine setup is missing, a dialog offers
to run it with one administrator prompt. **Uninstall** in Installed apps removes the installed
files, PATH entry, shortcut and entry, and the Codex and Claude Code registrations that run this
installation. It asks before quitting a running Anode or undoing machine setup.

**Installing from an agent's desktop app.** Windows can keep new files created by a packaged app,
such as Claude or Codex for Windows, private to that app. The installer now reports when that
happens to the shortcut or the installation instead of claiming success; run it from your own
terminal to fix it.

**Upgrading.** Before updating, save seat work, run `anode quit` (it closes every program in the
seat) and close MCP clients using Anode; the installer refuses to replace a running executable.
Run the new `install.ps1`: an existing installation gains its Start menu shortcut and Installed
apps entry. Start Anode again so both the daemon and seat host use the new build. MCP tools, CLI
commands and their arguments are unchanged.

**Validation and limits.** The candidate passes all 51 local quick checks, including hidden viewer
checks for the connecting screen and narrow layout, and package, installation, uninstallation and
agent-unregistration checks in disposable folders without real client settings. The viewer was
reviewed in offscreen renders of every state. Live checks of the new viewer and Start menu launch
against a running seat are pending. Broader Windows, DPI, screen-reader and client compatibility
checks remain outstanding. See the
[validation record](https://github.com/skulitom/Anode/blob/v0.7.0/docs/RELEASE-READINESS.md).

Requires 64-bit Windows 10/11 Pro, Enterprise or Education, or Windows Server with a Remote Desktop
host. Windows Home is unsupported. ARM64 is not validated; this package targets x64.
`setup` asks for administrator permission to enable Remote Desktop and child sessions.
It does not add firewall rules; it can restart Remote Desktop Services.

The installer verifies **SHA256SUMS** before installing the archive. Binaries and scripts are
currently unsigned; Windows may display a SmartScreen warning. Checksums detect corruption and do
not establish publisher identity. Review the source and your organization's policy before running.

See [installation, updates and removal](https://github.com/skulitom/Anode/blob/main/docs/INSTALL.md),
[agent configuration](https://github.com/skulitom/Anode/blob/main/docs/CONNECTING-AGENTS.md),
and the [changelog](https://github.com/skulitom/Anode/blob/main/CHANGELOG.md).
