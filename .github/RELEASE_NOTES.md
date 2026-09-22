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

## What's new in 0.8.0

**Agents take turns without bookkeeping.** Several agents share one background desktop, one at a
time. When another agent is using it, `seat_lease` acquire now waits in a first-come line, for up
to 30 seconds by default (`waitSeconds`, up to 300; `anode lease acquire --wait` on the CLI),
instead of failing at once. An agent keeps its place only while it keeps asking, so the desktop
never goes to an agent that gave up or went away.

**Leases that follow the work.** Each desktop action extends the lease, so an agent that keeps
working no longer renews; `renew` covers long pauses. Desktop tools take the lease themselves when
the desktop is free, and never start a seat to do it. Pointer, keyboard and gamepad input that
arrives without a lease is refused once with a request to observe first, because it was aimed at a
screen another agent may have changed. An MCP session whose identity Anode generated releases its
lease when it ends; a stable `ANODE_AGENT_ID` still keeps it for recovery.

**See who has the desktop.** Lease status, `seat_status`, `anode status` and the viewer's footer
name the owner (the MCP client's name, such as Claude Code or Codex, or `ANODE_AGENT_NAME`) and
list who is waiting. While others wait, the owner's desktop results say so, so it can hand the
desktop on.

**Upgrading.** Save seat work, run `anode quit` (it closes every program in the seat) and close the
agent sessions that use Anode, then install 0.8.0 and start Anode again. The lease protocol gained
`waitSeconds`, `agentName` and `startSeat`, so a 0.8.0 MCP server cannot take leases from an older
running daemon. Run `anode configure` to update the installed skill. Over MCP, acquire now waits up
to 30 seconds while another agent works; pass `waitSeconds: 0` for the previous immediate answer.

**Validation and limits.** The candidate passes all 53 local quick checks, including new checks of
the line, lapsed places, owner names, activity-extended leases, and MCP lease taking, look-first
input and hand-off with real MCP servers over private pipes, plus package, installation and
distribution checks. After release, the same code passed live checks on a real seat: two agents
waiting in line, handing over and observing after a take; the native and browser suites with real
input; and the 0.7.0 viewer. Start menu launch from a new installation and the pointer-isolation
script remain. See the
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
