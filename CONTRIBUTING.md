# Contributing to Anode

Anode is a Windows-only .NET 10 application. Read [AGENTS.md](AGENTS.md) and
[the architecture](docs/ARCHITECTURE.md) before changing session or input behavior.

## Build and verify

From the repository root in PowerShell:

```powershell
dotnet build Anode.sln --nologo
& .\src\Anode\bin\Debug\net10.0-windows\win-x64\anode.exe selftest --quick | Out-Host
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest -Package
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-install.ps1
```

Check `$LASTEXITCODE` after each command. Quick checks and installer tests do not start a seat,
inject desktop input, change user PATH or modify real agent settings. Live tests are opt-in;
see [development testing](docs/DEVELOPMENT-TESTING.md). Full `selftest` captures the current desktop,
tests Task Scheduler and exercises a machine-wide gamepad.

Keep changes focused and describe what changed, why, and which checks passed. For bugs, include
the Anode version, Windows edition/build, `doctor` output and the smallest reproduction. Review
logs for personal information before attaching them.

## Build while Anode is running

A running Anode daemon or MCP client started from `dist\anode.exe` locks it, so the default build
cannot replace it. Build and package into separate folders instead, then test that package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -OutputDirectory artifacts\pkg-build -ArchiveDirectory artifacts\pkg-release -Package
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-install.ps1 -ArchiveDirectory artifacts\pkg-release
```

`test-install.ps1` installs into a temporary folder, exercises the installer's update, rollback and
refusal paths, and runs the connector against fake client homes. It never touches your PATH, Start
menu, real client settings or the running daemon. Add `-QuickTest` to the build for the private-pipe quick
self-test; `-Test` runs the full self-test. Use a separate `git worktree` for parallel work so
builds do not share `src\Anode\obj` and `bin`.

A release build talks to the installed Anode's control pipe. While a daemon is running, do not run
`start`, `up`, `lease` or any other seat command from it: it would act on the running seat. Debug
builds, and any build given `--channel dev`, are a [separate dev Anode](docs/DEVELOPMENT-TESTING.md#a-separate-dev-anode)
that cannot reach it, and that refuses to start its own seat while the running one is up.

## Viewer and tray design

`Daemon/ViewerTheme.cs` holds the viewer's colors, fonts, renderer and title-bar colors; high-contrast
mode keeps the system look. To review a change without a seat or a visible window:

```powershell
& .\src\Anode\bin\Debug\net10.0-windows\win-x64\anode.exe __viewer-preview --out artifacts\ui-preview | Out-Host
```

It writes PNGs of the viewer, title bar included, in sample states (ready, the size it opens at,
hover, control, connecting, failed sign-in with details, minimum width) and of the tray menu. It
never connects.
Each view comes from a cloaked window far off-screen that has no taskbar button and cannot take
focus, and a view is skipped unless Windows confirms the cloak.

## Icon and social preview

`assets/anode.svg` is the master mark. `python assets/make-assets.py` regenerates
`anode-512.png`, `anode.ico` and `social-preview.png` directly from its geometry and colors (Python
3, Pillow and Segoe UI). Edit the SVG's named rectangles and pointer polygon; there is no second
copy of the mark to update in the script. The executable, viewer and tray use `anode.ico`.
The diagonal cursor is mirrored about `x = y`; its stem has parallel edges and a perpendicular tail.
Use `python assets/make-assets.py artifacts/icon-review --proof` to review the icons at actual
sizes on light and dark backgrounds before regenerating the checked-in assets.

## Release process

See [the release-readiness review](docs/RELEASE-READINESS.md) for the current evidence and outstanding
live checks. Passing the package checks alone does not establish desktop/input compatibility.
`RuntimeFrameworkVersion` in the project pins the self-contained runtime. Review it against
[Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) before each
release, update to the current supported patch, and rebuild and test the package.

1. Update the version in `src/Anode/Anode.csproj`, rename `## Unreleased` in `CHANGELOG.md` to the
   version and date, and update pinned install examples such as `docs/INSTALL.md`.
2. Update `.github/RELEASE_NOTES.md`, which is the user-facing release body, including its
   `What's new in` heading.
3. When tools or commands changed, bring the tool count in `README.md`, `docs/USAGE.md` and
   `docs/PROTOCOL.md`, the PROTOCOL.md tool mapping, `llms.txt`, the instructions in
   `src/Anode/Mcp/AgentGuide.cs`, the [command reference](docs/USAGE.md#command-reference) and the
   release notes in line with the code.
4. Set the new version in `server.json`, `.claude-plugin/plugin.json` and
   `packaging/mcpb/manifest.json`. Where a listing repeats the one-line description, keep it
   identical: "Background Windows desktop for AI agents: native GUI automation, screenshots and
   app testing."
5. Run the checks above plus `scripts\test-distribution.ps1` and `scripts\check-docs.ps1`, push the
   changes and confirm Windows CI passes.
6. Tag the tested commit with the matching version, such as `v0.6.0`, and push the tag.
7. After the release is published, set the version, the archive URL and its SHA-256 from the
   release's `SHA256SUMS` in `bucket/anode.json` and `packaging/winget/*` (winget expects the hash
   in upper case), then push. Scoop users get the update from that commit.

The Release workflow checks the tag against the project version, builds a self-contained x64
binary, runs quick and installation checks, then publishes the ZIP, installer and SHA-256 sums.
It can also be dispatched for an existing version tag. It does not overwrite existing releases.
Release publishing requires repository contents write permission. Normal build jobs are read-only.

The archive has a stable asset name, `anode-windows-x64.zip`, for download links and installers.
`SHA256SUMS` covers that archive and `install.ps1`. Checksums are not signatures; code signing and
package-manager listings can be added separately when their distribution requirements are met.
The Scoop bucket and the Claude Code plugin marketplace are served from this repository. The MCP
Registry, winget and MCP Bundle manifests are maintained here but not yet published.
