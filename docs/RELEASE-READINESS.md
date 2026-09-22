# Release readiness — 22 September 2026

**Scope: 0.8.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks. It changes how agents share the desktop
lease, the lease protocol and agent guidance; it does not change input delivery, capture or seat
isolation. Publication uses the existing unsigned release model and remains gated by Windows CI and
the Release workflow. The coverage limits below remain explicit.

The running seat was in long use by other agents while 0.8.0 was prepared, so every lease check ran
against private pipes: the real lease, daemon and MCP server code, with injected dispatch instead of
a desktop. The candidate has not yet run against a live seat, and neither have the 0.7.0 viewer
changes.

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

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 53 quick checks pass in the 0.8.0 Debug and Release builds, with no build warnings. New checks cover the line, lapsed places, names, waiting counts, activity-extended leases, helper acquisition never starting a seat, and MCP lease taking, look-first input, waiting through a release and hand-off on session end. | Push, run Windows CI against the final release commit and retain its results. |
| Multi-agent behavior | Exercised with real MCP server instances and the real lease over private pipes. | Run two or more real agents (for example Claude Code and Codex) against a live 0.8.0 seat; confirm they wait, hand over, read the notes and observe after a take. |
| Viewer | Unchanged since 0.7.0 apart from the owner label in the footer, reviewed offscreen. | The 0.7.0 live viewer checks remain: the connecting screen handing over to a seat and returning on disconnect, icons, the tray menu and Start menu launch. |
| Packaging and installation | The 0.8.0 package passes distribution and documentation checks, and disposable installation, update, rollback, shortcut, Installed apps, uninstallation and agent registration checks without modifying real client settings, the Start menu or Installed apps. | Install from a user's own terminal on a clean supported machine; exercise an actual client, not only the fake client CLIs. |
| Compatibility | Mixing versions is documented: a 0.8.0 MCP server cannot take leases from an older running daemon. | Upgrade daemons and agent sessions together, as the release notes say. |
| Native desktop, browser input and pointer isolation | Unchanged since 0.6.0, whose live native and browser checks passed; quick checks still exercise the pointer guard. | The dedicated live pointer-isolation script remains outstanding. |
| Runtime servicing | The project pins .NET 8.0.31, which the 0.6.0 review found current on 22 September 2026. | Keep servicing the bundled runtime and plan migration before .NET 8 support ends on 10 November 2026. |
| Publisher trust | 0.8.0 continues the unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes identify 0.8.0. | Tag the tested commit and publish its CI-built artifacts, then update Scoop/winget with the published checksum. |

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process). Windows allows one Anode seat per
Windows session, so live checks need a moment when the running seat can be closed. Then quit it,
start the candidate, and run two agents against it alongside the viewer checks above. Test the
candidate daemon and host together, not a new CLI against an older running daemon. The final
package must be retested after code changes.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
