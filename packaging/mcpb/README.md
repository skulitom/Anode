# Anode MCPB bundle (Claude Desktop extension)

`manifest.json` describes `anode-windows-x64.mcpb`, an [MCPB](https://github.com/modelcontextprotocol/mcpb)
bundle that installs Anode's MCP server into Claude Desktop on Windows. The bundle is **not published**:
it is not a release asset, it is not listed in `SHA256SUMS`, and the user documentation does not mention
it. It becomes a release asset only after the Claude Desktop test below passes.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -OutputDirectory artifacts\pkg-build -ArchiveDirectory artifacts\pkg-release -Package -Mcpb
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-distribution.ps1 -ArchiveDirectory artifacts\pkg-release
```

`-Mcpb` writes `anode-windows-x64.mcpb` next to the release ZIP. It contains:

| Entry | Source |
|---|---|
| `manifest.json` | this folder's `manifest.json`, with `version` taken from `src/Anode/Anode.csproj` |
| `server/anode.exe` | the executable built for the release ZIP |
| `LICENSE` | the repository license |
| `icon.png` | `assets/anode-512.png`, only when that file exists; otherwise `icon` is removed from the manifest |

The build needs only the .NET 8 SDK. CI uploads the bundle with the workflow artifacts for manual testing.
`scripts/test-distribution.ps1` checks the entry names, the manifest fields, the version and that the
tool list equals the `tools/list` reply. To run the official validator (it downloads Node packages),
validate the extracted bundle, not this folder: the validator requires the file named by `icon`, and
this folder has no `icon.png`.

```powershell
$null = New-Item -ItemType Directory -Path artifacts\mcpb-check -Force
tar -xf artifacts\pkg-release\anode-windows-x64.mcpb -C artifacts\mcpb-check
npx -y @anthropic-ai/mcpb@2.1.2 validate artifacts\mcpb-check
```

When a tool is added, removed or renamed, update `tools` here in the same change.

## Claude Desktop test (release gate)

Use a Windows 10/11 Pro, Enterprise or Education machine or VM (x64). Test both the MSIX (Microsoft
Store) build and the standard installer build of Claude Desktop when possible.

1. Install the bundle: double-click `anode-windows-x64.mcpb`, or use Settings > Extensions >
   Advanced settings > Install Extension.
2. Confirm that Claude Desktop lists all 32 tools and that `anode_guide` answers before any setup.
3. Run machine setup once from an administrator terminal with the bundled executable, for example
   `& "<extension folder>\server\anode.exe" setup | Out-Host`, then run `doctor` the same way from a normal
   terminal.
   The extension folder is under `%APPDATA%\Claude\Claude Extensions` (standard build) or under
   `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\Claude Extensions` (MSIX build).
4. Ask Claude to call `seat_status`, then `seat_lease` with `action=acquire`. A cold acquisition must
   start the daemon through Task Scheduler from the extension's `server\anode.exe` and succeed; note how
   long it takes and whether Claude Desktop times out first. Then run `seat_capabilities`, open Notepad
   with `seat_run` and observe it.
5. Restart Claude Desktop without stopping the seat. With the default Agent ID (`claude-desktop`),
   `seat_lease` with `action=status` must report `claude-desktop` as the owner, and acquiring again must succeed.
6. Release the lease, run `anode quit` with the bundled executable, then update and remove the extension.
   The daemon runs from the extension folder and locks it while it runs.

If step 2 fails on the MSIX build, check the reported `${__dirname}` virtualization problem
(anthropics/claude-code#47977): the command may point at the virtual `%APPDATA%` path while the files
live under `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc`. The reported workaround is to create
`%APPDATA%\Claude\Claude Extensions` before installing the extension. Record the result and any
workaround in `docs/CONNECTING-AGENTS.md` before publishing.

## Notes for directory reviewers

- Anode needs Windows 10/11 Pro, Enterprise or Education (not Home) or Windows Server with an RDP host,
  and a one-time `anode setup` as administrator. The bundle cannot perform that step.
- `seat_start` or `seat_lease` with `action=acquire` signs in a Windows child session, which can take a few
  minutes on a cold machine. Running `anode start --hidden` first avoids client timeouts.
- Desktop tools require a lease: call `seat_lease` with `action=acquire` first, and renew it before it
  expires (default 120 seconds).
- The gamepad tools need the [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) driver; without
  it they return an explanatory error.
- The MCP server has no telemetry, update check or remote endpoint, and the seat uses loopback RDP.
  Screenshots and UI text go only to the MCP client that asked for them. The manifest's privacy policy
  is `docs/PRIVACY.md`; see also the [security model](https://github.com/skulitom/Anode/blob/main/docs/SECURITY.md).
