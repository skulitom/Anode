# Release readiness

## 0.11.2 release validation — 10 October 2026

**Scope:** the fixes merged since 0.11.1 through #32, the quick-test cleanup in #33, the version
and release metadata in #30, and the Windows PowerShell 5.1 registry-entry default-path fix.
The Business licence proposal (#19) and the experimental hosted-runner seat workflow are excluded.
No tools or commands were added; the tool count stays 41. Product code was tested at `7b229e9`;
subsequent release-record and package-listing changes do not change that code.

### Live validation

The installed self-contained 0.11.2 daemon and host ran on Windows 11 Pro 25H2 build 26200, main
channel, child session 2, with an initial display of 1280x720 at 100%. Windows refused automatic
sign-in (disconnect reason 2055); the user signed in through Windows, after which the verified
seat became ready. The viewer was hidden for all application checks. Capture was available and
no known input blocker was reported. The installed instance was upgraded from 0.11.0, and was
left running after owned fixtures were closed and the desktop lease released.

| Area | Evidence |
| --- | --- |
| Native desktop | `test-desktop.ps1` passed discovery, UI Automation, password omission, text replacement/read-back, consumed-snapshot refusal, invocation, delayed-state waits, checkbox and list actions, sliders, read-only filtering, window movement and HTML inspection. |
| Chrome controls | `test-chrome-form.ps1` passed with Chrome 154.0.8037.99. Text-field replacement read back in 584 ms. An ignored dropdown value was refused; the open list skipped three repeated controls, exposed its options and successfully selected one. No warnings. |
| Browser input and command jobs | `test-development.ps1 -VerifyInput` passed stdout/stderr, nonzero exit, working-directory/environment and process-session checks. Headed Chrome passed form submission, HTTP response, canvas capture, mobile layout and page-error checks. Actual Anode mouse, key and text delivery was confirmed in the page. |
| File Explorer | An owned Explorer window exposed 99 controls through `inspect`, without the null-reference failure fixed in #20, and was closed afterward. |
| Displays | `test-display.ps1` applied 1024x768 at 100%, 1920x1080 at 150%, 1080x1920 at 100% and 2560x1440 at 125% in 153–171 ms. Status and screenshot dimensions matched; reset restored 1280x720 at 100%. |
| Environment | `selftest --no-gamepad` ran as an Anode command job inside the seat: all 102 checks passed, including screen capture, geometry and a disposable scheduler probe in that same session. |
| Virtual controller | Xbox 360 attach returned `seatOnly=true` and `verifiedFromDesktop=true` before any input. Stick movement, button tap, reset and detach passed with HidHide. This verifies isolation and command handling, not support in every game. |
| Android | `test-android.ps1 -Avd Pixel_9_Pro` passed a read-only API 36 start with automatic GPU selection, adb boot confirmation, a correctly sized screenshot, seat placement and clean emulator shutdown. |

Local results, logs, screenshots and inspection reports are retained in the validation checkout
under `artifacts/validation` (ignored by Git). All tests preserved the hidden viewer and the seat
session. The remaining seat is available for normal use.

### Build and package validation

- Debug build: no warnings or errors. All **99 quick self-tests** passed in Debug and Release.
- Documentation: 40 documents checked against 41 tools; links, anchors and tool names consistent.
- Disposable installer checks passed installation, update, rollback, refusal paths, agent
  configuration and uninstall. Release-mode distribution checks passed for the ZIP, MCPB bundle,
  checksums and generated registry entry. Both registry metadata files passed the publisher's
  schema validation.
- The registry-entry default output path was exercised successfully in Windows PowerShell 5.1
  and PowerShell 7 after fixing its parameter-binding issue.
- [Windows CI at 7b229e9](https://github.com/skulitom/Anode/actions/runs/38050181889) passed the
  build, packaging, quick suite, installation, distribution and documentation checks. CI repeats
  these checks on the recorded release commit, and the tag's Release workflow tests its package
  before publishing.
- The self-contained runtime remains **.NET 10.0.12**, checked against
  [Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
  on 10 October 2026.

### Coverage limits and known issues

- Audio was disabled on this seat, so live audio capture/playback was not repeated. The successful
  0.11.1 audio and legacy MCP audio runs remain recorded below; audio regression checks passed.
- The live pointer-isolation script, visible-viewer/control phases and machine-wide gamepad
  self-test were omitted to leave the user's foreground desktop available. The pointer-guard
  regression checks and the isolated seat-controller check passed.
- Chrome and Explorer results do not establish compatibility with every application. Steam games,
  DualShock 4, Android software rendering, writable AVDs and display reconnect fallback were not
  retested. Failed or uncertain operations remain covered by injected handlers/private pipes.
- The optional MCPB bundle remains a workflow artifact until its separate
  [Claude Desktop validation](../packaging/mcpb/README.md#claude-desktop-test-release-gate).
  Publishing the Windows release does not satisfy that gate or publish the installable registry entry.
- A Chrome window's first observation can omit the page; observe again with `maxDepth` 16.
  Windows' Open File security dialog may ignore `invoke`; focus it and click. `seat_window raise`
  may leave a UWP window behind another; use `focus`. `anode type` still lacks a pace option,
  and `anode.log` is not rotated.

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
