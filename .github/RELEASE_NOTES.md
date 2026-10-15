## What's new in 0.11.3

This patch fixes bugs found after 0.11.2, most of them by AI agents using Anode every day. No tools
or commands were added; there are still 41 tools.

- **Input waits for a look after Anode takes the lease.** When a lease lapsed and an agent's next
  click made Anode take it again, that click was refused with a request to observe first, but keys
  and text the client had already sent behind it ran, and could land on whatever window had come
  forward meanwhile. Now, from the moment Anode takes the lease for an agent, or the agent's lease
  ends, its pointer, keyboard and gamepad input is refused until it observes the desktop again.
- **Reconnect works on a seat that signed in with your password.** Such a seat's message said to
  press **Reconnect**, which then tried automatic sign-in and failed (disconnect reason 2055 on every
  press). Reconnect now asks for the password again, and the seat and its programs carry on. A
  detached seat's `seat_status`, screenshot error and `seat_display` reply now say why capture is
  down and what to do, and Windows' own disconnect causes are named.
- **A seat stopped during a display change stays stopped.** A `seat_display` change that failed as
  the seat was being stopped could report the stopped seat as `detached`, or carry its old display
  and advice into a seat started straight afterwards. It now answers "The seat was stopped while its
  display was changing."
- **A refused Reconnect leaves a running seat alone.** When Remote Desktop, its listener or child
  sessions stopped working under a running seat, **Reconnect** was refused but also set the seat to
  `error`: display reconnects then answered "stopped", and the next viewer disconnect dropped the
  seat. The viewer now says what is missing, and the seat and its agents carry on.
- **`anode selftest` no longer writes made-up events into your log.** Each quick self-test wrote
  about 110 lines from its stand-ins into `anode.log`, such as games launched and sign-ins refused.
  The checks now log to a temporary file, deleted when every check passes.
- **Log times read the same in every Windows language.** On Thai Windows the year came out as 2569,
  on Finnish Windows the time as 23.34.16. Log lines now always read `2026-10-13 23:34:16`, and
  screenshot file names and backup dates always use the Gregorian calendar.
- **`seat_window` `raise` says when it didn't take.** It answered `requested` while Windows left the
  window behind another. It now retries once and answers `raised: true`, or `raised: false` with the
  covering window in `coveredBy`; `focus` then brings the window forward.
- **`seat_key` knows the backtick key by the names agents try** (`grave`, `backtick`, `oem3`,
  `VK_OEM_3`) and other punctuation by plain names. `ctrl++`, which pressed ctrl alone and reported
  success, is refused; the plus key is `plus`.
- **`anode quit` ends Anode while the sign-in dialog is open,** instead of leaving it running and
  giving up after 60 seconds.

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
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#0113-release-validation).
Release builds now include .NET 10.0.13, Microsoft's October security update.
See the [changelog](https://github.com/skulitom/Anode/blob/v0.11.3/CHANGELOG.md#0113--2026-10-15) for every
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
agent sessions using Anode. Install 0.11.3, start Anode again and restart those agent sessions.
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
