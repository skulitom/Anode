## What's new in 0.11.1

This patch fixes issues found after 0.11.0 and adds live Windows validation of the merged changes.

- **Safer Android automation.** adb commands cannot change the shared server or replace/remove
  another device's forwarded local port, including through repeated `wait-for-*` prefixes. Emulator
  boot probes stay within the caller's reply deadline, even when adb hangs.
- **Reliable display recovery.** The viewer remembers the display Windows confirmed. A lost reply
  is never replayed or treated as success, and lease cleanup restores a change that finishes late.
- **Recovery after an agent is killed.** A generated MCP session's desktop lease ends when its
  process is gone and any admitted action finishes. Stable `ANODE_AGENT_ID` leases retain their
  existing release-or-expiry behavior.
- **Older MCP audio clients work.** Clients using protocol `2024-11-05` receive recordings as WAV
  resources; newer clients continue to receive native audio blocks.
- **MCP Registry publication.** Anode's initial metadata listing is live. Release CI now builds and
  attests the optional MCPB bundle for owner testing; it remains a workflow artifact until its
  separate Claude Desktop validation passes.

**Validation.** All 83 quick checks passed. Live on Windows 11 Pro build 26200, native desktop
controls, actual mouse/keyboard delivery to Chrome, portrait and scaled displays, audio playback
and recording, Android boot/capture/stop, hidden-viewer pointer isolation and a seat-only Xbox
controller passed. Killing an MCP client cleared its lease in 406 ms and restored the display
before the next client acquired it. Both legacy and modern MCP audio formats passed against the
running seat. Package, installation and distribution checks are also part of the release workflow.
See the [validation record](https://github.com/skulitom/Anode/blob/v0.11.1/docs/RELEASE-READINESS.md)
for evidence and remaining coverage limits, including the untested live display reconnect fallback.

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
agent sessions using Anode. Install 0.11.1, start Anode again and restart those agent sessions.
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
