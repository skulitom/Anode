# Release readiness — 24 September 2026

**Scope: 0.9.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks. It changes how Steam games launch, how MCP
names agents and how status describes the desktop lease, moves the runtime to .NET 10 and adds the
dev channel; it does not change input delivery, capture or seat isolation. Publication uses the
existing unsigned release model and remains gated by Windows CI and the Release workflow. The coverage
limits below remain explicit.

The user's agents were paused for the live checks, so its seat could be closed and the candidate's
own seats started. Each new seat needed the user's password, because the account signs in with a PIN.

## Findings addressed in this review

- Launching a Steam game in the seat started a Steam client there, which took Steam over from the
  user's desktop, and it did so on every launch. Only the names of the two objects a game uses to find
  its client are bound to a session. The seat host now links those names in the seat's namespace to
  the client wherever it runs, and starts the game itself against that client.
- Steam's own record of the signed-in account appears before a game could use it and survives an
  unclean exit, so a game started as soon as Steam claimed an account failed to initialize. Launches
  now ask the client the way a game does before starting one.
- Every session of one MCP client had the same name, and status described the lease to a placeholder
  caller, so an agent holding the desktop took its own lease for another agent's. Agents are now named
  after their client and project folder, and status speaks to the agent asking.
- .NET 8 support ends on 10 November 2026. Anode now runs on .NET 10, supported until November 2028.
- A development build shared the installed Anode's pipes and could stop it. Debug builds are now a
  separate dev channel.

## Live validation — 23 September 2026

Debug and Release builds of the candidate's code ran on Windows 11 Pro build 26200, with the user
present.

- **Steam.** With Steam not running, the first launch started Steam in session 1 and Liftoff in the
  seat, but Liftoff's `SteamAPI_Init` failed. The launch had trusted Steam's record of the account,
  left over from a client closed with the previous seat, 20 seconds before Steam signed in. A probe in
  the seat with the same environment then initialized against Steam. With the readiness check, from a
  new seat after Steam had exited cleanly, `anode steam 410340` returned after 39 seconds. Steam
  started in session 1 and Liftoff in the seat. Steam listed Liftoff as its game process, and
  Liftoff's menu showed the user's profile. Steam never left session 1. See the
  [investigation record](../bugreports/2026-09-23-steam-stays-on-the-desktop.md).
- **Agent names.** Two MCP sessions, started in two project folders and identifying as Claude Code and
  Codex, were named after their folders. The first took the desktop and was told it holds it; the
  second was told which agent does, in status and in the refusal of its click. The seat's log
  recorded both hand-overs.
- **.NET 10.** `scripts/test-desktop.ps1` passed its 15 checks. `scripts/test-pointer-isolation.ps1`
  passed with a visible, view-only viewer: the guard suppressed all 7 seat cursor moves, forwarded
  none, and the real pointer never jumped. `scripts/test-development.ps1 -VerifyInput` stopped before
  its browser checks, because a GameInput service helper held the new seat's foreground, and its
  administrator repair was declined.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 59 quick checks pass in the 0.9.0 Debug and Release builds, with no build warnings. New checks cover text and binary KeyValues, launch entry choice, every Steam launch path against stand-ins, namespace links between private objects, workspace and client names, status as the asking agent sees it, and the hand-over record. | Confirm Windows CI on the release commit. |
| Steam | Quick checks above; a read-only run resolved three installed games as Steam does; Liftoff ran live in the seat against the desktop's Steam ([above](#live-validation--23-september-2026)). | Games wrapped in Steam's DRM stub, games that need the overlay or Steam Input, and Steam in offline mode. |
| Multi-agent behavior | Checks of names, caller-relative status and hand-over records; live with two MCP sessions. The 0.8.0 line and hand-off checks still pass. | Run Claude Code and Codex themselves, not only their MCP servers, through a longer shared session. |
| Viewer | Unchanged apart from the runtime. Its offscreen preview renders pixel for pixel as on .NET 8, and live seats signed in through the viewer's credential dialog. | Start menu launch from an installation made in the user's own terminal; the connecting screen returning after a disconnect. |
| Packaging and installation | The 0.9.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps, uninstallation and agent registration checks without modifying real client settings, the Start menu or Installed apps. | Install from a user's own terminal on a clean supported machine; exercise an actual client, not only the fake client CLIs. |
| Compatibility | The daemon's changes are additive: older MCP servers already send the agent ID that status now answers to, and `steam.launch` accepts their arguments. Mixing versions remains documented. | Exercise a 0.8.0 MCP server against the 0.9.0 daemon; upgrade daemons and agent sessions together, as the release notes say. |
| Native desktop, browser input and pointer isolation | On .NET 10 the native suite and the pointer-isolation script with a visible, view-only viewer passed live. | The browser suite with real input on .NET 10, which a GameInput helper blocked; the pointer-isolation script's hidden-viewer and control phases (`-IncludeVisible`, `-IncludeControl`). |
| Runtime servicing | The project targets .NET 10 and pins 10.0.12, the current patch of that long-term support release on 23 September 2026. The quick checks, the Task Scheduler hand-off and screen capture pass on it. | Keep servicing the bundled runtime; .NET 10 is supported until 14 November 2028. |
| Publisher trust | 0.9.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.9.0. | Tag the tested commit, publish its CI-built artifacts, then update Scoop and winget with the published checksum. |

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
