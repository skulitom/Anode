# Release readiness — 22 September 2026

**Scope: 0.7.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging, installation and uninstallation checks. It changes the viewer, the
double-click behavior and installation integration; it does not change MCP tools, CLI commands,
input delivery or seat isolation. Publication uses the existing unsigned release model and remains
gated by Windows CI and the Release workflow. The coverage limits below remain explicit.

The 0.6.0 daemon and seat were in long use by another agent while 0.7.0 was prepared, so every
viewer check ran without a seat: hidden viewer checks, and renders of every viewer state from a
cloaked, off-screen window (`anode __viewer-preview`). The candidate has not yet run against a live
seat.

## Findings addressed in this review

- Windows Search could not find Anode: the installer created no Start menu shortcut, and opening
  `anode.exe` only explained how to use a terminal. The installer now adds a shortcut, and opening
  Anode shows the viewer, starting Anode, a stopped seat or its missing setup first.
- Run from an agent's packaged desktop app, the installer's Start menu shortcut was stored in the
  app's private storage and never reached Windows Search, while the installer reported success. It
  now resolves where Windows stored the shortcut and the executable and warns.
- Removal was a five-step manual procedure that left agent registrations pointing at a deleted
  executable. Installed apps now runs `uninstall.ps1`, which removes only what the installer
  recorded and registrations that run the removed executable, and never signs out a running seat
  to undo setup.
- The viewer showed the Remote Desktop control's blank window until it connected. A connecting
  screen now covers it only while no connection exists, driven by the control's own events.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 51 quick checks pass in the 0.7.0 Debug and Release builds, with no build warnings. | Push, run Windows CI against the final release commit and retain its results. |
| Viewer | Hidden checks cover labels, full-screen Stop and exit, the minimum-width layout and the connecting screen's state and hand-off. Offscreen renders cover ready, hover, control, connecting, disconnected, failed sign-in, minimum width and the tray menu, including the real Windows 11 title bar. | Run the candidate daemon and viewer against a seat: the connecting screen must give way to the seat, including a sign-in screen, and return on disconnect; check icons, hover and checked states, full screen and the tray menu live. |
| Start menu launch | Launch logic is covered by the quick CLI checks only. | Open Anode from the Start menu with Anode running, stopped and not running; confirm the viewer comes to the front. |
| Packaging and installation | The 0.7.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps registration, uninstallation and agent registration/unregistration checks without modifying real client settings, the Start menu or Installed apps. The private-storage warning was exercised from Claude for Windows with scratch folders. | Install from a user's own terminal on a clean supported machine, then uninstall through Settings; exercise an actual client, not only the fake client CLIs. |
| Native desktop, browser input and pointer isolation | Unchanged since 0.6.0, whose live native and browser checks passed; quick checks still exercise the pointer guard. | As for 0.6.0, the dedicated live pointer-isolation script remains outstanding. |
| Windows and accessibility | The dark palette keeps text contrast above 4.5:1; the connecting screen exposes its state to screen readers. | Check keyboard and screen-reader navigation, high contrast and 100–200% DPI on supported Windows configurations. |
| Runtime servicing | The project pins .NET 8.0.31, which the 0.6.0 review found current on 22 September 2026. | Keep servicing the bundled runtime and plan migration before .NET 8 support ends on 10 November 2026. |
| Publisher trust | 0.7.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.7.0. | Tag the tested commit and publish its CI-built artifacts, then update Scoop/winget with the published checksum. |

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process). Install the candidate package from
your own terminal, not from an agent's desktop app. When the running seat is free, save its work,
run `anode quit`, open Anode from the Start menu and check the viewer rows above against the live
seat; then uninstall and reinstall through Settings once. Test the candidate daemon and host
together, not a new CLI against an older running daemon. The final package must be retested after
code changes.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
