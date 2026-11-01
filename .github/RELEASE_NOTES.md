## What's new in 0.11.5

This patch updates the .NET runtime that release builds include. Anode's behaviour, tools and
commands are unchanged; there are still 41 tools.

- **.NET 10.0.14.** Release builds include .NET 10.0.14, Microsoft's security update of
  25 October 2026, which fixes CVE-2026-111497, CVE-2026-70197, CVE-2026-102870 and CVE-2026-114148.
  0.11.4 included 10.0.13. If you run 0.11.4, upgrade to get these fixes; nothing else changes.
- **Dependencies.** Seat audio uses NAudio.Wasapi 3.3.0 (was 3.2.0), and `anode setup`'s Remote
  Desktop service handling uses System.ServiceProcess.ServiceController 10.0.14 (was 10.0.13).

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
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#0115-release-validation).
See the [changelog](https://github.com/skulitom/Anode/blob/v0.11.5/CHANGELOG.md#0115--2026-11-01) for every
change.

## Install or upgrade

Download **anode-windows-x64.zip** for the portable executable, agent connector and docs. Release
builds include .NET 10.0.14; no SDK or runtime installation is needed.

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
agent sessions using Anode. Install 0.11.5, start Anode again and restart those agent sessions.
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
