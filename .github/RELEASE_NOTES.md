## Install Anode on Windows

Download **anode-windows-x64.zip** below for the portable executable, agent connector and docs.
Release builds include .NET; no SDK or runtime installation is needed.

For a per-user installation with PATH setup, download **install.ps1** and run it in PowerShell:

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

## What's new in 0.5.0

**Desktop leases.** `seat_lease` and `anode lease` give one agent exclusive use of the seat, with
renewal, idle expiry and scoped release. Desktop tools now require a lease: MCP clients acquire one
automatically, CLI workflows pass an agent ID and token. Stale queued actions are rejected while
in-flight operations finish, and Stop stays immediate and global.

**The viewer no longer moves the real mouse pointer.** A program in the seat calling `SetCursorPos`
(SDL games do it on every switch between relative and absolute mouse mode) teleported the pointer on
your own desktop, even with the viewer hidden or view-only. Anode now gates that call inside its own
process, and `anode status` reports `pointerGuard`.

**Sign in…** beside **Reconnect** in the viewer header retries a failed automatic sign-in, or
requests the Windows credential dialog, without restarting Anode or closing the seat's programs.

**Upgrading:** restart existing daemon and seat-host processes after installing, so they load the
new protocol and the pointer guard.
See [the agent guide](https://github.com/skulitom/Anode/blob/main/docs/FOR-AGENTS.md).

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
