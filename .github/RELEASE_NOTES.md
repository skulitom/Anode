## What's new in 0.11.2

This patch fixes bugs found after 0.11.1, most of them by AI agents using Anode every day.

- **Typing keeps pace with the program.** `seat_type` and `anode type` sent characters as fast as
  Windows took them, so a busy program, such as Chrome editing a long field, could drop and reorder
  them while the call reported success. Characters now go out 15 ms apart by default, and typing
  pauses while the program is busy; `perCharMs` sets the pace. A call that stops partway reports
  exactly how many characters were typed.
- **`set_value` checks that the value took.** On a Chrome drop-down list (`<select>`), `set_value`
  reported success while the list kept its old choice. Anode now reads the control back and fails,
  saying to expand the list and pick the option, if the control still shows its old value.
- **Observation copes with more windows.** `seat_observe` failed on File Explorer windows ("Object
  reference not set to an instance of an object"), and repeated a Chrome window's tree while one of
  its drop-down lists was open. Both are fixed; a control listed twice now appears once.
- **Queued requests keep their deadline.** A request that waited behind a long operation, such as
  typing, could lose its reply and leave the seat `detached`. It now answers, or fails, in time.
- **Browser sign-in is clearer.** `anode browser --sign-in` explains its window in its first tab
  and opens several addresses. When the seat's profile is open on the other desktop, `seat_browser`
  and `--sign-in` refuse and name the process holding it, instead of reporting a window.
- **Faster startup.** CLI and MCP skip their first connection wait when no daemon is running, and
  waiting callers wake as soon as the seat is ready ([measurements](https://github.com/skulitom/Anode/blob/v0.11.2/docs/STARTUP-PERFORMANCE.md)).
- **Clearer records of what Anode keeps.** A daemon started by a desktop app that redirects AppData,
  such as the Claude desktop app, keeps its files in that app's package folder, which the privacy,
  install and troubleshooting pages now say; `anode rendering --restore` finds a backup saved there.
  The uninstaller ends with everything it leaves on the machine and how to remove each item.

**Known issues.** A Chrome window's first observation can come back without the page, and the
page's controls sit deeper than the default `maxDepth` of 8: observe again with `maxDepth` 16. On
Windows' "Open File - Security Warning" dialog, `invoke` reports success without running anything:
focus the dialog with `seat_window`, then click. `seat_window` `raise` can leave a UWP app behind
the foreground window: use `focus`. `anode type` has no option for a slower pace. Fixes are planned
for a later release.

**Validation.** All 97 quick checks and the documentation check passed, and Windows CI repeated
the build, packaging, installation and distribution checks. The live checks in a Windows 11 seat
and their limits are in the
[validation record](https://github.com/skulitom/Anode/blob/v0.11.2/docs/RELEASE-READINESS.md).
See the [changelog](https://github.com/skulitom/Anode/blob/v0.11.2/CHANGELOG.md#0112--2026-10-09) for every
change.

## Install or upgrade

Download **anode-windows-x64.zip** for the portable executable, agent connector and docs. Release
builds include .NET 10.0.12; no SDK or runtime installation is needed.

For a per-user installation, download **install.ps1** and run it in PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

It adds Anode to your PATH, the Start menu and **Settings → Apps → Installed apps**. For a first
installation, open a new terminal and run:

```powershell
anode doctor | Out-Host
anode setup | Out-Host
anode configure | Out-Host
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

`configure` finds installed Codex and Claude Code CLIs and backs up their settings. Restart the
agent afterward. Other MCP clients can launch `anode.exe` with the argument `mcp`.

**Upgrading:** save seat work, run `anode quit` (which closes every program in the seat), and close
agent sessions using Anode. Install 0.11.2, start Anode again and restart those agent sessions.
Run `anode configure` to refresh the installed skill.

Requires 64-bit Windows 10/11 Pro, Enterprise or Education, or Windows Server with a Remote Desktop
host. Windows Home is unsupported; ARM64 is not validated. `setup` asks for administrator permission
to enable Remote Desktop and child sessions and can restart Remote Desktop Services.

The installer verifies **SHA256SUMS**. Binaries and scripts remain unsigned, so Windows may display
a SmartScreen warning. Checksums detect corruption; build attestations identify the release
workflow. Anode is desktop isolation for trusted agents, not a security sandbox.

See [installation, updates and removal](https://github.com/skulitom/Anode/blob/main/docs/INSTALL.md),
[agent configuration](https://github.com/skulitom/Anode/blob/main/docs/CONNECTING-AGENTS.md),
and the [changelog](https://github.com/skulitom/Anode/blob/main/CHANGELOG.md).
