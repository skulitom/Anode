## What's new in 0.11.4

This patch fixes bugs found after 0.11.3, the first of them reported by an outside user. No tools
or commands were added; there are still 41 tools.

- **`anode doctor` warns when Windows signs local accounts in as Guest.** With Windows' "Guest
  only" sharing model (`ForceGuest = 1` under `HKLM\SYSTEM\CurrentControlSet\Control\Lsa`, set by
  some security suites and hardened images), the seat could never sign in, even with the password
  the lock screen accepts, while every doctor row read `[ok]` and the seat's error was only
  "extended reason 0". Doctor now has a **Local account sign-in** row that warns while it is set, a
  failed seat sign-in names it, and
  [TROUBLESHOOTING](https://github.com/skulitom/Anode/blob/main/docs/TROUBLESHOOTING.md#windows-refuses-the-right-password-for-a-local-account)
  explains how to check it. Anode never changes the setting.
  ([#57](https://github.com/skulitom/Anode/issues/57))
- **`anode display` says why it can't read the seat's status.** With nothing to change, it exited 1
  and printed nothing when its status request failed. It now prints the reason on stderr.
- **`anode type`, `anode steam` and `anode gamepad` refuse bad arguments before connecting.** With
  Anode stopped, text too long for one call, a bad `--exe`, a gamepad `--ms` out of range or an
  unknown button answered "Anode is not running" (exit 1) instead of the real error (exit 2). An
  unknown gamepad button, axis or trigger is now refused at once, with the names that work, on the
  CLI and over MCP, where `gamepad_tap` and `gamepad_set` used to take the desktop lease first.
- **A piped command no longer waits for Anode to exit.** When Anode couldn't be started through the
  Task Scheduler, the daemon it started instead inherited the caller's handles, so a command such
  as `anode lease acquire | Out-String` waited until Anode itself exited, and
  `anode browser --sign-in | Out-Host` until the browser was closed. Both now start with no
  inherited or standard handles.
- **A log line waits longer for other writers before it is dropped.** When several Anode processes
  wrote to `anode.log` at once, a line waited only about 100 ms for the others; it now waits up to a
  second.
- **The skill asks before it installs anything.** The `anode-desktop` skill and `anode_guide` show
  the user the exact install commands and wait for their yes. They download the installer from a
  named release, not `latest`, and check its SHA-256 before running it.

**Known issues.** A Chrome window's first observation can come back without the page, and the
page's controls sit deeper than the default `maxDepth` of 8: observe again with `maxDepth` 16. On
Windows' "Open File - Security Warning" dialog, `invoke` reports success without running anything:
focus the dialog with `seat_window`, then click. `anode type` has no option for a slower pace; use
`seat_type` with a larger `perCharMs`. `anode.log` grows until you delete it. Fixes are planned for
a later release.

**Validation.** The quick checks, the documentation check and Windows CI (build, packaging,
installation, distribution, and the environment self-test on a GitHub-hosted Windows runner) passed
on the release commit. The owner's live check, `scripts\release-check.ps1` (native controls, Chrome
forms, real input, File Explorer and display changes on Windows 11), runs on the release commit
before it is tagged; its summary and coverage limits are in the
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#0114-release-validation).
Release builds include .NET 10.0.13, Microsoft's October security update.
See the [changelog](https://github.com/skulitom/Anode/blob/v0.11.4/CHANGELOG.md#0114--2026-10-21) for every
change.

## Install or upgrade

Download **anode-windows-x64.zip** for the portable executable, agent connector and docs. Release
builds include .NET 10.0.13; no SDK or runtime installation is needed.

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
agent sessions using Anode. Install 0.11.4, start Anode again and restart those agent sessions.
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
