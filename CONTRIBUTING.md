# Contributing to Anode

Anode is a Windows-only .NET 8 application. Read [AGENTS.md](AGENTS.md) and
[the architecture](docs/ARCHITECTURE.md) before changing session or input behavior.

## Build and verify

From the repository root in PowerShell:

```powershell
dotnet build Anode.sln --nologo
& .\src\Anode\bin\Debug\net8.0-windows\win-x64\anode.exe selftest --quick | Out-Host
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

## Release process

1. Update the version in `src/Anode/Anode.csproj`, `CHANGELOG.md` and any pinned install examples.
2. Review `.github/RELEASE_NOTES.md`, which is the user-facing release body.
3. Run the checks above, push the changes and confirm Windows CI passes.
4. Tag the tested commit with the matching version, such as `v0.4.0`, and push the tag.

The Release workflow checks the tag against the project version, builds a self-contained x64
binary, runs quick and installation checks, then publishes the ZIP, installer and SHA-256 sums.
It can also be dispatched for an existing version tag. It does not overwrite existing releases.
Release publishing requires repository contents write permission. Normal build jobs are read-only.

The archive has a stable asset name, `anode-windows-x64.zip`, for download links and installers.
`SHA256SUMS` covers that archive and `install.ps1`. Checksums are not signatures; code signing and
package-manager listings can be added separately when their distribution requirements are met.
