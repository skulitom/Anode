# Release readiness — 25 September 2026

**Scope: 0.10.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks. It keeps virtual controllers inside the seat
with HidHide and opens the viewer fitted to the seat; it does not change keyboard and mouse input,
capture or the seat's session isolation. Publication uses the existing unsigned release model and
remains gated by Windows CI and the Release workflow. The coverage limits below remain explicit.

The live checks ran on the user's machine after HidHide 1.5.230 was installed and Windows restarted,
while no agent was using the seat. Each new seat needed the user's password, because the account
signs in with a PIN.

## Findings addressed in this review

- A virtual controller is a device on the machine, so a game on the user's desktop read the input an
  agent sent to Liftoff in the seat: an agent's flight controller steered No Man's Sky. The seat host
  now jails every device of each seat pad to the seat's session with HidHide, lists the names new pads
  take before they take them, checks each attach from the user's desktop and unplugs a controller the
  desktop can still open. See the
  [investigation record](../bugreports/2026-09-25-virtual-pad-reaches-desktop-games.md).
- The viewer's opening size was the seat's plus a fixed allowance, so at 100% scaling the seat area
  was 16 pixels wider than the seat and the Remote Desktop control padded the scaled seat with white
  strips. The viewer now measures its frame and bars and keeps the control at the seat's shape.

## Live validation — 25 September 2026

The installed Release build of the candidate ran on Windows 11 Pro build 26200 with HidHide 1.5.230
and ViGEmBus 1.17.333.

- **Without a seat.** The full self-test's live check plugged in a neutral pad jailed to an unused
  session: XInput and HID opens from the desktop were refused, and its HidHide entries were removed
  afterward.
- **In a seat.** The seat host logged that HidHide was active for its session. `gamepad attach`
  returned `seatOnly: true` and `verifiedFromDesktop: true`. One probe ran on both sides: in the seat
  the controller was in XInput slot 0 and both of its devices opened; on the desktop XInput saw no
  controller and both devices refused the open with access denied. A pad plugged in with `vgamepad`
  from a seat job, as Haltere's flight controller does, behaved the same and was reported with owner
  `seat`.
- **Clean-up.** Stopping the seat made the daemon remove the 100 HidHide entries the seat had kept.
- **Viewer.** The installed build opened with a 1280x720 seat area filled edge to edge by the Remote
  Desktop control. The offscreen preview shows the seat centred on the dark background in a window of
  another shape.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 61 quick checks pass in the 0.10.0 Debug and Release builds, with no build warnings. New checks cover HidHide's multi-strings, jail entries, reserved controller names with the device names recorded under them, the user's own HidHide entries and settings, ended seats, ViGEm programs outside the seat, controller attach against stand-ins, and the fitted viewer. | Confirm Windows CI on the release commit. |
| Virtual controllers | Quick checks above; live isolation of Anode's controller and of a seat program's own pad, verified from the desktop by XInput and device opens ([above](#live-validation--25-september-2026)). | A device name Windows has never used, which HidHide can hide only once Windows announces it; games in the seat that read controllers only through GameInput's system service, which runs outside the seat; DualShock 4 pads; HidHide releases after 1.5.230. |
| Steam | Quick checks; in the 0.9.0 review a read-only run resolved three installed games as Steam does, and Liftoff ran live in the seat against the desktop's Steam ([record](../bugreports/2026-09-23-steam-stays-on-the-desktop.md)). Unchanged in 0.10.0. | Games wrapped in Steam's DRM stub, games that need the overlay or Steam Input, and Steam in offline mode. |
| Multi-agent behavior | Checks of names, caller-relative status and hand-over records, live with two MCP sessions in the 0.9.0 review. The 0.8.0 line and hand-off checks still pass. | Run Claude Code and Codex themselves, not only their MCP servers, through a longer shared session. |
| Viewer | Opens fitted to the seat, verified live with a 1280x720 seat at 100% scaling. The hidden viewer check and the offscreen preview cover windows of other shapes. | High-DPI monitors and a seat larger than the screen, live; Start menu launch from an installation made in the user's own terminal; the connecting screen returning after a disconnect. |
| Packaging and installation | The 0.10.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps, uninstallation and agent registration checks without modifying real client settings, the Start menu or Installed apps. The installer, run with the candidate's package, updated a 0.6.0 installation and registered Claude Code and Codex. | Install from a user's own terminal on a clean supported machine; exercise an actual client, not only the fake client CLIs. |
| Compatibility | The daemon's changes are additive: `seat.pad-visibility` is new, and `gamepad.attach` and `gamepad.state` add fields. Older MCP servers and CLIs keep working against the 0.10.0 daemon; the seat host keeps controllers in the seat whichever client attaches them. | Exercise a 0.9.0 MCP server against the 0.10.0 daemon; upgrade daemons and agent sessions together, as the release notes say. |
| Native desktop, browser input and pointer isolation | On .NET 10 the native suite and the pointer-isolation script with a visible, view-only viewer passed live. | The browser suite with real input on .NET 10, which a GameInput helper blocked; the pointer-isolation script's hidden-viewer and control phases (`-IncludeVisible`, `-IncludeControl`). |
| Runtime servicing | The project targets .NET 10 and pins 10.0.12, still the current patch of that long-term support release on 25 September 2026. The quick checks, the Task Scheduler hand-off and screen capture pass on it. | Keep servicing the bundled runtime; .NET 10 is supported until 14 November 2028. |
| Publisher trust | 0.10.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.10.0. | Tag the tested commit, publish its CI-built artifacts, then update Scoop and winget with the published checksum. |

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process). Windows allows one Anode seat per
Windows session, so live checks need a moment when the running seat can be closed, and a new seat asks
for the user's password when they sign in to Windows with a PIN. Then quit it, start the candidate or a
Debug build, which is the separate [dev channel](DEVELOPMENT-TESTING.md#a-separate-dev-anode), and run
two agents against it alongside the viewer checks above. Test the candidate daemon and host together,
not a new CLI against an older running daemon. The final package must be retested after code changes.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
