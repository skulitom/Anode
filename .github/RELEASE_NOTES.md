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

## What's new in 0.9.0

**Steam stays on your desktop.** A Steam game launched through Anode (`anode steam <appid>`,
`steam_launch`) now runs in the seat and uses the Steam client already running on your desktop, which
stays there. Before, a launch started a Steam client in the seat, and that took Steam over from your
desktop. When Steam is not running, Anode starts it on your desktop, minimized, and starts the game
once Steam could serve it. The game's program comes from Steam's own launch configuration; `--exe`
(MCP: `exe`) names another. Games started this way run without the Steam overlay and Steam Input.
`--force` launches through a Steam client in the seat, as before.

**Agents can tell who has the desktop.** MCP names each agent after its client and project folder,
for example "Claude Code in WebShop", instead of giving every session of a client one name. An agent
asking about a lease it holds is told "You hold the desktop lease", so it no longer mistakes its own
lease for another agent's. The log records who took, released or let the desktop expire.

**.NET 10.** Anode moves to .NET 10, the current long-term support release, before .NET 8 support
ends on 10 November 2026. The download still includes the runtime, so no .NET installation is needed.

**A separate dev Anode.** Debug builds run as their own `dev` channel, with separate pipes, logs and a
viewer labelled "(dev)", so a development build cannot reach or stop the installed Anode. Windows
allows one seat per session, so only one channel runs a seat; the others say which one has it.

**Upgrading.** Save seat work, run `anode quit` (it closes every program in the seat) and close the
agent sessions that use Anode, then install 0.9.0 and start Anode again. Older MCP servers work with
the 0.9.0 daemon and are told when they hold the desktop, but only 0.9.0 MCP servers name agents
after their project folder. Run `anode configure` to update the installed skill. `steam_launch` no
longer refuses while Steam runs on your desktop, and `force` now means launching through a Steam
client in the seat.

**Validation and limits.** The candidate passes all 59 local quick checks, including new checks of
Steam launch configurations, every launch path, namespace links, agent names and status as the asking
agent sees it, plus package, installation and distribution checks. Live on a real seat, Liftoff ran
in the seat against the Steam client on the desktop, which never moved, and two MCP agents in
different project folders were told apart. On .NET 10, the native desktop suite and the
pointer-isolation script passed. The browser suite with real input did not run, because a GameInput
helper held the new seat's foreground. Games wrapped in Steam's DRM stub or needing its overlay are
untested. See the
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#live-validation--23-september-2026).

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
