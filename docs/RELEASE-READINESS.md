# Release readiness — 22 September 2026

**Scope: 0.8.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks. It changes how agents share the desktop
lease, the lease protocol and agent guidance; it does not change input delivery, capture or seat
isolation. Publication uses the existing unsigned release model and remains gated by Windows CI and
the Release workflow. The coverage limits below remain explicit.

The running seat was in long use by other agents while 0.8.0 was prepared, so its lease checks first
ran against private pipes: the real lease, daemon and MCP server code, with injected dispatch instead
of a desktop. Once that seat was free, the 0.8.0 code, built from main together with the unreleased
dev channel, passed [live checks](#live-validation--23-september-2026) on a real seat, including the
0.7.0 viewer.

## Findings addressed in this review

- Agents carried the lease's bookkeeping: acquire, renew before a two-minute expiry, poll after
  `seat_busy` and wait out the lease of any session that disconnected. Contention had no queue.
  Acquire now waits in a first-come line whose places lapse five seconds after their agent stops
  asking; each desktop action extends the lease; MCP desktop tools take a free desktop without
  starting a seat; and sessions with generated identities release on exit.
- Owners were identified only by random IDs. Status, errors and the viewer now name the owner and
  the agents waiting, from the MCP client or `ANODE_AGENT_NAME`.
- Taking a lease on an agent's behalf could have let input aimed at an old screen land on another
  agent's work. Pointer, keyboard and gamepad input after such a take is refused once with a
  request to observe first.

## Live validation — 23 September 2026

Release and Debug builds of commit 191ca20 ran on Windows 11 Pro build 26200, with the user present.

- **Seat and viewer.** A new seat needed the user's password in Windows' dialog, because the account
  signs in with a PIN. From **Sign in…** in the tray menu, the seat went from connecting to ready in
  about 17 seconds. The user reviewed the connecting screen, the hand-off to the seat and the tray
  menu, and reported no problems.
- **Native and browser suites.** `scripts/test-desktop.ps1` passed its 15 checks.
  `scripts/test-development.ps1 -VerifyInput` passed its 5 command-job and 8 browser checks,
  including Anode mouse and keyboard input reaching a form in headed Chrome.
- **Two agents.** Two MCP servers identified as Claude Code and Codex shared the seat:
  - a desktop tool took the free desktop and said so;
  - status named the owner and the agent waiting, and the owner was told another agent waited;
  - Codex got the desktop 362 ms after Claude Code released it;
  - a desktop tool was refused at once while Codex held the desktop, naming Codex;
  - ending Codex's session freed the desktop;
  - the first input after taking the desktop was refused with a request to observe, then worked, and
    the typed text reached the app.
- **Compatibility.** A 0.6.0 MCP server checked status, took and released the lease, took a
  screenshot and listed windows through the new daemon.
- **Dev channel.** A Debug build refused to start a seat beside the running 0.6.0 seat. With that seat
  closed, its own seat ran on `anode-control-dev` and `anode-seat-dev`, its seat host started with
  `--channel dev`, and it took a leased screenshot. While it ran, the main build refused and named the
  dev Anode: `start`, `lease acquire`, `status`, MCP `seat_start`, and `anode up` run directly, which
  refused 36 ms after starting, before creating a viewer.
- **Finding.** Windows 11 Notepad, opened for the two-agent check, restored the user's own open and
  unsaved documents in the seat. It was closed without editing them. The security and troubleshooting
  guides and the agent skill now describe apps that restore a session.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 53 quick checks pass in the 0.8.0 Debug and Release builds, with no build warnings. New checks cover the line, lapsed places, names, waiting counts, activity-extended leases, helper acquisition never starting a seat, and MCP lease taking, look-first input, waiting through a release and hand-off on session end. Windows CI passed on the release commit, 13309bf. | None. |
| Multi-agent behavior | Exercised with real MCP server instances over private pipes, then live with two MCP servers on a real seat ([above](#live-validation--23-september-2026)). | Run Claude Code and Codex themselves, not only their MCP servers, through a longer shared session. |
| Viewer | Unchanged since 0.7.0 apart from the owner label in the footer. The user reviewed the live connecting screen, sign-in, hand-off to the seat and tray menu. | Start menu launch from an installation made in the user's own terminal; the connecting screen returning after a disconnect. |
| Packaging and installation | The 0.8.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps, uninstallation and agent registration checks without modifying real client settings, the Start menu or Installed apps. | Install from a user's own terminal on a clean supported machine; exercise an actual client, not only the fake client CLIs. |
| Compatibility | Mixing versions is documented: a 0.8.0 MCP server cannot take leases from an older running daemon. A 0.6.0 MCP server works with the new daemon. | Upgrade daemons and agent sessions together, as the release notes say. |
| Native desktop, browser input and pointer isolation | Unchanged since 0.6.0. The native and browser suites, including real input, passed live again; quick checks still exercise the pointer guard. On 23 September a .NET 10 build passed `scripts/test-pointer-isolation.ps1` with a visible, view-only viewer: all 7 seat cursor moves were suppressed and the real pointer never jumped. | Run the hidden-viewer and control phases (`-IncludeVisible`, `-IncludeControl`). |
| Runtime servicing | 0.8.0 shipped on .NET 8.0.31. Since then the project targets .NET 10 and pins 10.0.12, the current patch of that long-term support release on 23 September 2026, because .NET 8 support ends on 10 November 2026. On .NET 10 the quick checks, the Task Scheduler hand-off and screen capture pass, and the viewer's offscreen preview renders pixel for pixel as it did on .NET 8. Live, a .NET 10 dev seat signed in with the user's password and passed the native suite's 15 checks. | The browser suite with real input: a GameInput helper held the new seat's foreground, and its administrator repair was declined. Keep servicing the bundled runtime; .NET 10 is supported until 14 November 2028. |
| Publisher trust | 0.8.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.8.0. v0.8.0 is tagged at 13309bf and published, and Scoop and winget point to it. | None. |

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
