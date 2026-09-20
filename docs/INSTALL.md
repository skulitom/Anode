# Install, update and remove Anode

## Requirements

- Windows 10/11 Pro, Enterprise or Education, or Windows Server with the Remote Desktop host.
  Windows Home cannot host a child session.
- The downloadable package targets Windows x64. ARM64 is not validated.
- PowerShell 5.1 or later for the installer and agent connector.
- Administrator permission for the separate, one-time `anode setup` step.
- No .NET installation for release builds. Building from source requires the .NET 8 SDK.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) only if you want a virtual gamepad.

## Recommended: install for your user

Download [install.ps1](https://github.com/skulitom/Anode/releases/latest/download/install.ps1),
inspect it if desired, and run from its folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

It downloads the latest release and its `SHA256SUMS` from GitHub, verifies the archive, runs
`anode version` to check the executable, installs in `%LOCALAPPDATA%\Programs\Anode` and appends
that folder to your **user** PATH. It uses no administrator prompt. Open a new terminal afterward;
restart your terminal application or sign out and in if it retains an old PATH.
You can always use the full path immediately:

```powershell
& "$env:LOCALAPPDATA\Programs\Anode\anode.exe" doctor | Out-Host
```

Useful options:

```powershell
# Choose a version and register installed Codex/Claude Code CLIs as well.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Version 0.5.0 -Client Auto
# A dedicated custom folder; leave PATH alone.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -InstallDirectory C:\Tools\Anode -NoPath
```

`-Client` accepts `None` (default), `Auto`, `Codex`, `Claude` or `Both`. Client registration failures
leave the installation in place; fix the reported client problem and rerun `anode configure`.
Use an empty folder for the first installation. The installer updates only folders bearing its
`.anode-install.json` marker and preserves unrelated files during updates.

## Portable or offline

Download [anode-windows-x64.zip](https://github.com/skulitom/Anode/releases/latest/download/anode-windows-x64.zip)
and [SHA256SUMS](https://github.com/skulitom/Anode/releases/latest/download/SHA256SUMS).
Compare `Get-FileHash .\anode-windows-x64.zip -Algorithm SHA256` with the ZIP entry in `SHA256SUMS`,
then extract the whole archive. Keep `connect-agents.ps1` next to `anode.exe` for `configure`.
The core CLI/MCP runtime can also run as a standalone executable.

To install from already downloaded files without network access:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -PackagePath .\anode-windows-x64.zip -ChecksumPath .\SHA256SUMS
```

Binaries and scripts are currently **unsigned**. SmartScreen or an organization's application
policy may block them. Checksums detect a corrupt or mismatched download, not publisher identity.
Get the files from this repository's releases and follow your organization's policy.

## One-time machine setup

```powershell
anode doctor | Out-Host
anode setup | Out-Host
anode doctor | Out-Host
```

A nonzero `doctor` result before setup means prerequisites are missing. Follow the reported fix;
it is not a failed installation. `setup` enables Remote Desktop and child sessions, asks for UAC
approval and may restart Remote Desktop Services. It adds no firewall rules. See
[the exact machine changes and undo behavior](SECURITY.md#what-anode-setup-changes).

For GPU rendering and a higher remote-session frame cap, opt in with
`anode setup --fps 60 --gpu`, then reboot if requested.

## Configure your agent

```powershell
anode configure | Out-Host
anode configure codex | Out-Host   # or choose one client
anode configure claude | Out-Host
```

The command detects CLIs on PATH, uses their registration commands, keeps timestamped backups
beside existing settings, configures a 420-second tool timeout, and installs the `anode-desktop`
skill for automatic selection in suitable desktop tasks. Add `--no-skill` for MCP registration
alone. Customized skills are preserved; [locations and update behavior](FOR-AGENTS.md) are documented.
`both` requires both CLIs and
checks they exist before changing either client. `auto` with neither installed prints guidance.
No approval policies change. No seat starts. Restart the agent to reload tools.
For manual configuration and Claude Desktop, see [Connecting agents](CONNECTING-AGENTS.md).

## Verify the installation

```powershell
anode version | Out-Host
anode selftest --quick | Out-Host
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

The first two commands do not start a seat. `start` creates the background session;
`capabilities` checks actual capture and known input blockers. If sign-in fails after Windows
Hello/PIN login, open the viewer from the tray icon and click **Sign in…** in the header to request
Windows' credential dialog. For a new daemon, use `anode start --sign-in`.
See [Troubleshooting](TROUBLESHOOTING.md) for readiness, capture and input problems.

## Update

Finish work in the seat, then stop Anode and close agent sessions that use its MCP server:

```powershell
anode quit | Out-Host  # closes every application in the seat; save work first
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Repeat your `-InstallDirectory` if you used a custom folder. The installer refuses locked files,
verifies the new package before copying, and rolls back file copies if replacement fails.
The stable executable path keeps client registrations working. Reopen the agent afterward.
Use `-Version` to choose a previous release with the same package format.
Portable users should extract a fresh folder, then run its `anode configure` to update paths.

## Remove

1. Save work, run `anode quit`, and close MCP clients using Anode.
2. Remove registrations with `codex mcp remove anode` and/or
   `claude mcp remove --scope user anode`. For other clients, remove only their Anode entry.
   Remove the `anode-desktop` skill folder from the [client skill locations](FOR-AGENTS.md#skill-installation-and-control)
   if installed; MCP registration and skills are independent.
3. Optionally run `anode rendering --restore` to restore the saved per-user RDP rendering value,
   and `anode setup --undo` to disable child sessions. Remote Desktop remains enabled;
   [security documentation](SECURITY.md) explains why and how to disable it separately.
4. Delete the installation folder you chose (default `%LOCALAPPDATA%\Programs\Anode`) and remove
   that exact entry from your user PATH in **Edit environment variables for your account**.
5. Logs and saved rendering state remain in `%LOCALAPPDATA%\Anode`. Keep them for troubleshooting
   or delete them after restoring settings. Client config backups remain beside the originals.

## Build from source

```powershell
git clone https://github.com/skulitom/Anode.git
cd Anode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest -Package
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install.ps1 -PackagePath artifacts\release\anode-windows-x64.zip -ChecksumPath artifacts\release\SHA256SUMS
```

See [Contributing](../CONTRIBUTING.md) for verification and releases.
