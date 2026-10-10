# Release readiness

## 0.11.2 candidate — 10 October 2026

**Scope:** the fixes merged since 0.11.1 (pull requests #10 to #32, except those still open, such
as #19, the Business edition's terms) and the startup changes in
[startup performance](STARTUP-PERFORMANCE.md). No new tools or commands; the tool count stays 41.
The candidate is the commit that sets the version to 0.11.2. Fixes merged after it are not in it.
The changelog dates 0.11.2 to 10 October, when its contents were fixed.

### Already checked, without a seat

- The candidate's tree (`main` at `98173de` plus the version change): all **99 quick checks** and
  the documentation check passed locally on 10 October, and so did the release-mode distribution
  check (`test-distribution.ps1 -Release`) on its package, MCPB bundle and registry entry. Windows
  CI passed on `98173de`: Release build, packaging and the MCPB bundle, registry-entry validation,
  quick self-tests, disposable install, update, rollback and uninstall, distribution and
  documentation checks. CI repeats them on the candidate.
- Code fixes came with regression checks, in the quick suite or in CI's install tests, and each
  pull request had a second, independent review before it merged.
- The .NET runtime pin is still the current 10.0 patch, **10.0.12** (Microsoft's release metadata,
  10 October 2026). If a newer 10.0 patch is out when you tag, update `RuntimeFrameworkVersion` first.
- Publishing: merging the version change doesn't publish anything. Anode is already listed in the
  MCP Registry (0.11.0, metadata only), so `publish-registry.yml` skips pushes to `main`; 0.11.2's
  installable entry follows the release and the Claude Desktop bundle test
  ([publishing](PUBLISHING.md)).

### Owner's live check (about 25 minutes)

These need a real seat, so only the owner runs them. Installing the candidate also upgrades the
installed Anode. Before you start: no game in the seat, nothing open there you need (`anode quit`
closes every program in it), and the .NET 10 SDK, Node.js and Chrome installed.

1. **Build the candidate** (about 5 minutes):

   ```powershell
   git clone https://github.com/skulitom/Anode anode-0.11.2
   cd anode-0.11.2
   $candidate = git log -1 --format=%H -S '<Version>0.11.2</Version>' -- src/Anode/Anode.csproj
   git checkout $candidate
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -OutputDirectory artifacts\pkg-build -ArchiveDirectory artifacts\pkg-release -QuickTest -Package
   ```

2. **Install it.** Close the agent apps and sessions that use Anode first (they keep `anode.exe`
   open, and the installer stops rather than replace a running copy). Then:

   ```powershell
   anode quit | Out-Host
   powershell -NoProfile -ExecutionPolicy Bypass -File artifacts\pkg-release\install.ps1 -PackagePath artifacts\pkg-release\anode-windows-x64.zip -ChecksumPath artifacts\pkg-release\SHA256SUMS
   ```

   In a new terminal, in the same folder:

   ```powershell
   anode version | Out-Host
   anode start --hidden | Out-Host
   anode capabilities | Out-Host
   ```

   `version` must say 0.11.2. Windows may ask you to sign in to the seat once.

3. **Run the live checks** (about 15 minutes), holding a lease:

   ```powershell
   $env:ANODE_AGENT_ID = 'release-0.11.2'
   $lease = anode lease acquire --ttl 600 | Out-String | ConvertFrom-Json
   if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
   $env:ANODE_LEASE_TOKEN = $lease.leaseToken
   $exe = (Get-Command anode).Source
   powershell -ExecutionPolicy Bypass -File scripts\test-desktop.ps1 -Anode $exe
   powershell -ExecutionPolicy Bypass -File scripts\test-chrome-form.ps1 -Anode $exe
   powershell -ExecutionPolicy Bypass -File scripts\test-development.ps1 -Anode $exe -InstallBrowserTools -VerifyInput
   anode run explorer.exe C:\Windows | Out-Host
   Start-Sleep -Seconds 2
   $explorer = @((anode windows --query Windows --json | ConvertFrom-Json).windows | Where-Object process -eq 'explorer')[0]
   if (-not $explorer) { throw 'No File Explorer window in the seat yet: wait a moment, then set $explorer again.' }
   anode inspect $explorer.windowId | Out-Host
   anode window $explorer.windowId close | Out-Host
   anode lease release | Out-Host
   ```

   | Check | What it covers in 0.11.2 | Passes when |
   | --- | --- | --- |
   | `test-desktop.ps1` | native controls, including `set_value`'s new read-back on a text box | it prints `"passed": true` |
   | `test-chrome-form.ps1` | Chrome: a text field's `set_value` reads back within its 1.5 s wait; `set_value` on a `<select>` fails instead of reporting success; the open list's observation lists each control once | it prints `"passed": true`; a warning that the open list's options weren't listed is a finding for a later release, not a failure |
   | `test-development.ps1` | typing's new pace, with mouse, key and text delivery checked in a headed Chrome page | it ends without an error |
   | File Explorer | `inspect` on an Explorer window, which failed in 0.11.1 | it lists the window's controls |

4. **Release.** If every check passed, tag the candidate and push the tag from the candidate
   checkout, whose `HEAD` is the candidate ([release process](../CONTRIBUTING.md#release-process),
   step 6):

   ```powershell
   git tag v0.11.2 HEAD
   git push origin v0.11.2
   ```

   The Release workflow builds, tests and publishes the release. After it: the package manifests
   (step 7), and the Claude Desktop test of the workflow's MCPB bundle
   ([release gate](../packaging/mcpb/README.md#claude-desktop-test-release-gate)), which lets the
   registry list 0.11.2 as installable. The validation results go into this record then.

### Covered without a live run

The seat profile held on the other desktop (Restart Manager), requests queued behind a long
operation, packaged apps' folders and the uninstaller's list are covered by quick checks with
stand-ins and by CI's disposable installs, not by a live seat. Startup timings are in
[startup performance](STARTUP-PERFORMANCE.md).

### Known and open, for a later release

- A Chrome window's first observation can come back without the page, and page controls sit below
  the default `maxDepth` of 8. Observe again with `maxDepth` 16.
- On Windows' "Open File - Security Warning" dialog, `invoke` on Run reports success, but nothing
  runs. Focus the dialog with `seat_window`, then click.
- `seat_window` `raise` can leave a UWP window behind the foreground window. Use `focus`.
- `anode type` has no option for a slower pace (MCP's `seat_type` has `perCharMs`).
- `anode.log` is never rotated ([privacy](PRIVACY.md)).

## 0.11.1 release validation — 2 October 2026

**Scope: 0.11.1 release validation.** All open PRs were reviewed, corrected where necessary and
merged after their Windows CI passed. The patch includes legacy MCP audio compatibility, shared
adb server/forward protections, bounded emulator startup, confirmed display memory and recovery
of leases owned by killed MCP processes. Publication remains gated by Windows CI and the Release
workflow.

### Live validation — 2 October 2026

The self-contained 0.11.1 candidate daemon and seat host ran together on Windows 11 Pro build
26200 in the separate `dev` channel, child session 3, initially 1280x720 at 100%. The user made
the seat available and signed in once after Windows refused automatic sign-in. Audio redirection
was enabled. The viewer was then hidden throughout these checks. No application tests used the
parent desktop. The environment self-test separately exercised parent screen capture and a
disposable scheduler process, as documented for non-quick self-tests.

| Area | Evidence |
| --- | --- |
| Native desktop | `test-desktop.ps1` passed window discovery, UI Automation, password omission, text replacement, consumed-snapshot refusal, invocation, waits, toggles, lists, slider bounds, window movement and HTML inspection. |
| Chrome and command jobs | `test-development.ps1 -VerifyInput` passed command exit codes/streams, cwd/env and process-session checks; headed Chrome, form submission, HTTP response, canvas capture and mobile layout; and actual Anode mouse, key and text delivery confirmed in the page. No page errors. A GameInput helper warning appeared, but delivery to this test application was verified. |
| Displays | `test-display.ps1` applied 1024x768 at 100%, 1920x1080 at 150%, 1080x1920 at 100% and 2560x1440 at 125%, all live in 157–184 ms. Status and screenshot dimensions matched, reset restored 1280x720 at 100%, and the viewer and session stayed unchanged. |
| Audio | `test-audio.ps1` played an 880 Hz tone and captured three seconds of 48 kHz stereo PCM WAV from Remote Audio. The measured tone amplitude was 0.1197, with one reported discontinuity and no timestamp errors. Playback was stopped after the test. |
| Android | `test-android.ps1 -Avd Pixel_9_Pro` booted the read-only API 36 AVD in 15.8 seconds with `gpu auto`; adb reported boot complete, a 1280x2856 screenshot showed the home screen, status placed the emulator in the seat, and `emu kill` stopped it. |
| Hidden-viewer pointer isolation | `test-pointer-isolation.ps1 -PlacePointer` ran with the real pointer inside the viewer rectangle. All seven attempted updates were suppressed, none forwarded, and the watcher recorded no correlated real-pointer jumps. The viewer stayed hidden. |
| Virtual controller | The candidate attached an Xbox 360 controller with `seatOnly=true` and `verifiedFromDesktop=true`; the parent-session open probe was refused. Stick movement, a button tap, reset and detach succeeded. HidHide was active and no other ViGEm program or virtual controller was present. This verifies isolation and command delivery, not game-specific controller support. |
| MCP audio compatibility | Real stdio clients negotiated `2024-11-05` and `2025-03-26`. Both recorded a 300 ms WAV from the running seat; the older client received an embedded resource with a RIFF payload and no audio block, and the newer client received an audio block. |
| Killed-client recovery | A generated MCP client acquired a 600-second lease and changed the display to 1024x768. Killing that process cleared its lease in 406 ms. A second client acquired the desktop only after the startup display was restored. The seat session stayed the same and the viewer remained hidden. |

Local logs, JSON results, screenshots, audio and the MCP integration harness are retained under
`artifacts/validation-0.11.1` (ignored by Git). The separate PR review record is under
`artifacts/pr-review-2026-10-02`.

### Automated validation

- The candidate passed all **83 quick self-tests** and documentation checks. The environment
  self-test (`selftest --no-gamepad`) passed **86 checks**, adding screen capture, geometry and
  scheduler coverage. Machine-wide controller injection was omitted from that suite because the
  separate live seat-controller check above exercised HidHide isolation.
- Regression checks cover nested adb wait prefixes and forwarded-port ownership; a hung adb
  boot probe exhausting its deadline; killed ephemeral clients versus stable identities; display
  changes that Windows refuses or clamps; and lost replies, reconnect failures and late display
  restoration. Timed-out actions are never replayed.
- The final package is rebuilt after the release metadata and audio tool-description update.
  Required checks are Debug build/quick tests, Release quick tests, documentation, MCPB and
  registry-entry generation, disposable installation/update/rollback/uninstall, and distribution
  validation. CI repeats packaging and installation before publishing the tagged release.
- The .NET 10 runtime pin remains **10.0.12**, verified against Microsoft's release metadata on
  2 October 2026. The package includes that runtime.

### Remaining coverage limits

- The live display reconnect fallback was not needed: every requested display applied live.
  Credential-dialog seats refuse reconnects that would need the password again. Failed, clamped,
  late and uncertain replies are covered with injected handlers and private pipes.
- Visible-viewer and focused-control pointer phases were not run; the hidden-viewer isolation
  phase passed. High-DPI parent monitors and games that reset their rendering need separate checks.
- Android software rendering, writable starts, signed-in web consoles and a real emulator outside
  the seat were not exercised in this run. Server-command and device-ownership refusals are covered
  by regression tests. The previously damaged Pixel 3a AVD was left untouched.
- GameInput service behavior and application accessibility vary. A successful Chrome input test
  does not establish compatibility with every game, secure desktop or custom UI. Steam games and
  DualShock 4 controllers were not retested in this patch.
- The optional MCPB bundle needs its separate
  [Claude Desktop validation](../packaging/mcpb/README.md#claude-desktop-test-release-gate) before
  public upload and an installable registry entry. The existing 0.11.0 metadata listing remains
  available. Local packaging checks do not substitute for that client test.
- Distribution remains unsigned. Checksums and GitHub build attestations do not provide an
  Authenticode signature. No clean-machine Windows compatibility certification, long-running
  soak test or comprehensive security audit is claimed.

### Live validation — 28-29 September 2026

The earlier 0.11.0 evidence, including Android Studio, seat-browser profile checks and diagnosis of
the damaged Pixel 3a image, is preserved in the
[0.11.0 validation record](https://github.com/skulitom/Anode/blob/v0.11.0/docs/RELEASE-READINESS.md#live-validation--28-29-september-2026).
The open issues recorded there are addressed by this patch.

Follow the [release process](../CONTRIBUTING.md#release-process). Anode remains desktop isolation
for trusted agents running as the user, not a security sandbox; the [security model](SECURITY.md)
and application/Windows limitations still apply.
