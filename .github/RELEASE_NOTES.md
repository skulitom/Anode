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

## What's new in 0.6.0

**A clearer viewer and a new icon.** The mark has a symmetric diagonal cursor. The viewer adds
consistent dark and high-contrast colors, clearer input and connection labels, and copyable status
details. Stop, Release control and Exit full screen remain visible in full screen. Connections
refuse to proceed if the desktop pointer guard cannot be installed.

**More reliable agent connections.** Duplicate JSON fields and malformed Unicode are rejected
without ending the connection. Cancelled requests cannot start or cancel jobs, while commands
already started remain recoverable. Command output uses UTF-8 and preserves Unicode characters
across buffer, pagination and history boundaries.

**Better agent discovery.** All 32 tools have titles, bounded argument schemas and explicit
annotations. Server instructions and the installed skill lead with the desktop lease workflow.
The `desktop_test` and `desktop_guide` prompts provide reusable workflows. Screenshot results no
longer duplicate their control trees, which could cause clients to discard the image.

**Installation and packaging.** Installation works when an agent runner omits the `OS` environment
variable. Packages include .NET 8.0.31, verified checksums and build-provenance attestations.
The Claude Code plugin marketplace and Scoop bucket provide additional installation options.

**Upgrading (breaking for scripts and agents):** Only `seat_start` and `seat_lease` acquisition
start a desktop over MCP. Status and diagnostics never start one, and stale leases cannot restart
Anode after the user quits. The CLI rejects unknown options and malformed arguments with exit code
2 before acting. Job `maxChars` counts Unicode code points; reuse output cursors exactly as returned.
Desktop work still requires a lease; see
[multiple agents](https://github.com/skulitom/Anode/blob/main/docs/MULTI-AGENT.md).

Before updating, save seat work, run `anode quit` (it closes every program in the seat) and close
MCP clients using Anode; the installer refuses to replace a running executable. Install the new
version, start Anode again so both the daemon and seat host use the new build, and run
`anode configure` to update the installed skill.
See [the agent guide](https://github.com/skulitom/Anode/blob/main/docs/FOR-AGENTS.md).

**Validation and limits.** The candidate passed all 51 local quick checks, package/installation
checks and live Windows Forms/WPF and Chrome tests on Windows 11 Pro 25H2, including delivered
mouse/keyboard input. The dedicated live pointer-isolation test was deferred to keep the user's
desktop/session available; its quick regression checks passed. Broader Windows, DPI, screen-reader
and client compatibility checks remain outstanding. See the
[validation record](https://github.com/skulitom/Anode/blob/v0.6.0/docs/RELEASE-READINESS.md).

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
