# Release readiness — 29 September 2026

**Scope: 0.11.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks. It adds Android emulators, Android Studio and
a signed-in browser in the seat, display changes for a running seat and agent audio. Apart from the seat
host and its inspection worker becoming DPI aware, it does not change keyboard and mouse input, capture
or the seat's session isolation. Publication uses the existing unsigned release model and remains gated
by Windows CI and the Release workflow. The coverage limits below remain explicit.

The live checks ran on the user's machine with the installed Release build of the candidate, in a seat
started with `anode start --sign-in`, while no other agent was using it. The seat needed the user's
password, because the account signs in with a PIN.

## Findings addressed in this review

- The seat host and its inspection worker were not DPI aware, so in a seat above 100% scaling Windows
  gave them a smaller, scaled screen: screenshots were not at the display's real resolution, and input
  and control bounds used scaled pixels. Both now run Per-Monitor V2 DPI aware. Live at 1920x1080 and
  150%, screenshots were 1920x1080 and the pointer moved to physical coordinates.
- A display change whose viewer reconnect failed was recovered at the requested display instead of the
  one the seat showed before. The seat host now asks the viewer back at the display it measured, and
  the daemon refuses reconnects for a seat that signed in through the credential dialog, which would
  need the password.
- Android Studio hands a second start on the same settings to the instance already running, in any
  session, so a Studio started in the seat on the user's settings opened its project on the user's
  desktop. Studio now runs on a profile of its own, and `run` refuses its launchers.
- `scripts/test-android.ps1` failed in Windows PowerShell 5.1, which leaves `$PSScriptRoot` empty in the
  parameter defaults of a script with a mandatory parameter. It now finds its folder from the script's
  own path.

## Found and not fixed in 0.11.0

Compass's anode-fixer reviewed the Android and display commits and opened fixes that await the
maintainer's review. Merged together onto 0.11.0's code, they build and pass all quick checks and the
documentation check.

- [#5](https://github.com/skulitom/Anode/pull/5): `android_emulator`'s adb guard checks only the first
  argument, so a command after a `wait-for-*` prefix, `raw`, `track-devices` and some `forward` forms
  reach the shared adb server or other devices' forwards. An agent's own shell can run adb directly in
  any case; the guard keeps tool calls from doing so by mistake.
- [#6](https://github.com/skulitom/Anode/pull/6): after a display change that Windows did not apply and
  a refused reconnect, the daemon keeps the failed display and asks for it at the viewer's next
  connection.
- [#3](https://github.com/skulitom/Anode/pull/3): `seat_audio_listen` answers with an audio content block,
  which MCP clients on protocol 2024-11-05 reject along with the whole result.

## Live validation — 28-29 September 2026

The installed Release build of the candidate ran on Windows 11 Pro build 26200 in seat session 4, at
1280x720 and 100%, with Android emulator 36.2.12, Android Studio 2025.2.1 and an NVIDIA GeForce RTX 4090.

- **Displays.** `scripts/test-display.ps1` changed the running seat to 1024x768, 1920x1080 at 150%,
  1080x1920 and 2560x1440 at 125%. Windows applied each live in 156-173 ms, status and screenshots
  followed each, `reset` restored 1280x720 at 100%, and the session and viewer were unchanged. In the
  screenshots the 150% desktop was scaled and the portrait one laid out correctly; after one change the
  wallpaper had not repainted yet. Through MCP, `seat_display` returned its screenshot as an image. The
  reconnect fallback did not run: every change applied live, and this seat refuses it.
- **Native desktop.** `scripts/test-desktop.ps1` passed at 1280x720 and 100%, and at 1920x1080 and 150%,
  where a pointer move to (1900, 1060) landed there in physical pixels.
- **Android.** `scripts/test-android.ps1 -Avd Pixel_9_Pro` passed: the Pixel 9 Pro (API 36, Google Play)
  booted from its quick-boot snapshot in 12.2 s, `gpu auto` rendered on the graphics card, adb reported
  it booted with a 1280x2856 screen, the screenshot had that size and showed the home screen,
  `android_status` listed it in the seat, and `emu kill` stopped it. Through MCP, the server listed 41
  tools and 4 prompts, `android_status` listed the three AVDs, `adb kill-server` was refused, the
  emulator's window was in the seat, and `stop` shut it down.
- **Read-only starts.** A read-only emulator kept its disk overlays in
  `%LOCALAPPDATA%\Temp\AndroidEmulator` and deleted them when it stopped. A quick boot wrote to the
  AVD's disk images as it loaded the snapshot; cold boots left them untouched.
- **A failing AVD.** `Pixel_3a_API_34_extension_level_7_x86_64` (API 34) never booted. The first start,
  a read-only quick boot, resumed with adb offline. Cold boots restarted about every 80 seconds for 10
  minutes: adb came online with `sys.system_server.start_count` 1 and dropped again. The AVD's kernel log
  (`data\misc\pstore\pstore.bin`) shows `EXT4-fs error ... verity file corrupted` from `system_server`,
  then `Kernel panic`: a fault in the AVD's data image. Anode started it read-only throughout; wiping or
  recreating it is left to the user. [Android](ANDROID.md#when-an-emulator-does-not-finish-booting) now
  describes both failures.
- **Android Studio.** `android_studio` opened Studio in the seat on `%LOCALAPPDATA%\AnodeAndroidStudio`.
  It reached its Welcome screen with no setup wizard, and closing its window ended it.
- **Seat browser.** `anode browser https://example.com` opened Chrome in the seat on
  `%LOCALAPPDATA%\AnodeChrome`, with its window in the seat, and `seat_browser` through MCP opened the
  same profile. While Chrome ran on the desktop, `run` refused `chrome.exe` and `https://example.com`.
  Signing in with `--sign-in` did not run: it needs the user.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 78 quick checks pass in the 0.11.0 Release build, with no build warnings. New checks cover display requests, their restoration and DPI-aware measurement; SDK, AVD and running-emulator discovery; emulator starts, boot waits and seat-only adb with its refusals; Studio's and the browser's profiles; the launch guards; and the audio tools, all with stand-ins. | Confirm Windows CI on the release commit. |
| Displays | Four displays applied live in 156-173 ms and reset, and the native suite passed at 150% ([above](#live-validation--28-29-september-2026)). | The reconnect fallback, in a seat started without the credential dialog; a display Windows refuses or changes (#6); games that reset their rendering. |
| Android | A Pixel 9 Pro AVD booted, captured and stopped in the seat; Studio and the seat browser opened on their own profiles; `adb kill-server`, Chrome's usual profile and links were refused. | Software rendering; a writable start; an emulator outside the seat, which every Android action must refuse; `--sign-in` and a signed-in Play Console; the adb guard's gaps (#5). |
| Audio | Quick checks of clips, endpoint isolation, lease gating, MCP presentation and playback lifetime. | Live playback and recording in a seat started with `--audio`; MCP clients on protocol 2024-11-05 (#3). |
| Virtual controllers | Verified live in the 0.10.0 review, recorded in this file's history; unchanged in 0.11.0. | A device name Windows has never used; games that read controllers only through GameInput's system service; DualShock 4 pads; HidHide releases after 1.5.230. |
| Steam | Quick checks; Liftoff ran live in the seat against the desktop's Steam in the 0.9.0 review ([record](../bugreports/2026-09-23-steam-stays-on-the-desktop.md)). Unchanged in 0.11.0. | Games wrapped in Steam's DRM stub, games that need the overlay or Steam Input, and Steam in offline mode. |
| Multi-agent behavior | Checks of names, caller-relative status and hand-over records, live with two MCP sessions in the 0.9.0 review. A display change is restored when its agent's lease ends, covered by quick checks. | Run Claude Code and Codex themselves, not only their MCP servers, through a longer shared session; a lease that expires during a display change, live. |
| Viewer | Opens fitted to the seat (0.10.0). Display changes left the viewer's visibility and the session unchanged. | High-DPI monitors and a seat larger than the screen, live, including the viewer fitting a portrait seat; the connecting screen returning after a disconnect. |
| Packaging and installation | The 0.11.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps, uninstallation and agent registration checks without modifying real client settings, the Start menu or Installed apps. The installer, run with the candidate's package, updated a 0.9.0 installation. | Install from a user's own terminal on a clean supported machine; exercise an actual client, not only the fake client CLIs. |
| Compatibility | The daemon's changes are additive: `seat.display` is new, and status adds `startupDisplay` and the screen's `scale`. Older MCP servers and CLIs keep working against the 0.11.0 daemon, without the new tools. | Exercise a 0.10.0 MCP server against the 0.11.0 daemon; upgrade daemons and agent sessions together, as the release notes say. |
| Native desktop, browser input and pointer isolation | The native suite passed live at 100% and at 150%. | The browser suite with real input on .NET 10; the pointer-isolation script's hidden-viewer and control phases (`-IncludeVisible`, `-IncludeControl`). |
| Runtime servicing | The project targets .NET 10 and pins 10.0.12, still the latest patch of that long-term support release on 29 September 2026. | Keep servicing the bundled runtime; .NET 10 is supported until 14 November 2028. |
| Publisher trust | 0.11.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.11.0. | Tag the tested commit, publish its CI-built artifacts, then update Scoop and winget with the published checksum. |

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process). Windows allows one Anode seat per
Windows session, so live checks need a moment when the running seat can be closed, and a new seat asks
for the user's password when they sign in to Windows with a PIN. Then quit it, start the candidate or a
Debug build, which is the separate [dev channel](DEVELOPMENT-TESTING.md#a-separate-dev-anode), and run
two agents against it alongside the viewer checks above. Test the candidate daemon and host together,
not a new CLI against an older running daemon. The final package must be retested after code changes.
For the next release, run the manual checks that #3, #5 and #6 list once they are merged.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
