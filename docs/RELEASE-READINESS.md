# Release readiness — 22 September 2026

**Scope: 0.6.0 release validation, not a broad production certification.** The candidate passes its
local automated build, packaging and installation checks, plus native-app and browser input tests.
Publication uses the existing unsigned release model and remains gated by Windows CI and the Release
workflow. The coverage limits below remain explicit; a release does not certify every Windows
configuration or agent client.

On 22 September, the initial background sign-in failed with disconnect reason 2055. After an
authorized background-session restart and interactive Windows sign-in, both the 0.6.0 daemon and
host ran successfully on Windows 11 Pro 25H2 (build 26200). Native and browser checks passed in
the new session. The desktop lease was released and further interactive checks were deferred when
the user needed the session for another agent; that running session was left available.

## Findings addressed in this review

- Duplicate JSON fields and invalid Unicode escapes could throw during deferred parsing, disconnecting
  MCP clients or reaching a pipe handler before rejection. Both transports now validate the complete
  message before dispatch, including nested objects, arrays and escaped property names, and recover
  on the same connection.
- Already cancelled execution requests could register/start a command or cancel an existing job.
  Cancellation is now checked before these effects. Commands already started remain recoverable.
- Execution output depended on the Windows console encoding despite a documented UTF-8 contract.
  Decoding is now explicit, and Unicode characters remain intact across stream buffers, paginated
  reads and retained-history boundaries, including a one-character page limit.
- Replaced the mark with a new square amber tile and a cursor mirrored about its diagonal axis.
  The SVG drives the ICO, PNG and social card; small exports are reviewed on light and dark surfaces.
- A missing pointer guard previously only logged an error before RDP connected. Connections and
  reconnects now stop before Connect when guard installation fails. Partial installation and changed
  import targets cannot report `installed: true`. Quick checks exercise refused/allowed connection
  callbacks and the real RDP import gate in an unconnected, hidden viewer.
- The previous local package included .NET 8.0.5 because the installed SDK was 8.0.300. Self-contained
  builds now pin .NET 8.0.31. A clean NuGet package advisory scan did not detect the old embedded
  runtime; dependency scanning alone is not sufficient.
- Release-mode distribution checks now reject changes left in the Unreleased changelog section.
  The normal packaging check deliberately remains usable for development builds.

## Evidence and remaining work

| Area | Evidence | Remaining coverage / follow-up |
| --- | --- | --- |
| Build and protocol | All 51 quick checks pass in the 0.6.0 Debug and Release builds. Windows CI passed commit `11f1422`, before the version/notes update. | Run CI against the final release commit and retain its results. |
| Packaging and installation | The 0.6.0 package passes release-mode distribution checks, documentation checks, and disposable installation, update/rollback and client-configuration tests without modifying real client settings. | Install/upgrade the final package on a clean supported Windows machine; exercise an actual client, not only the fake client CLIs. |
| Native desktop and browser input | `test-desktop.ps1` passed all 15 native checks. `test-development.ps1 -VerifyInput` passed command jobs, headed Chrome, isolated browser context, form/HTTP behavior, canvas capture, mobile layout and confirmed Anode mouse/keyboard delivery. Both used the 0.6.0 daemon/host and preserved the viewer state. | Repeat on other supported Windows configurations and representative real applications; complete long-running reconnect/cancellation testing. |
| Pointer isolation | Quick tests exercise refused connections and the guard through the real RDP client's import slot. The live candidate reports the guard installed. | The dedicated live pointer-isolation script was deferred to leave the desktop/session available for the user's other agent. Live no-jump behavior and visible/control phases remain unverified for 0.6.0; incidental suppression counts are not a substitute for the test. |
| Windows and accessibility | Hidden rendering covers the viewer layout; the dark text palette exceeds 4.5:1 contrast. | Check keyboard and screen-reader navigation, high contrast and 100–200% DPI on supported Windows configurations. |
| Runtime servicing | The project pins the currently published .NET 8 patch. | Keep servicing the bundled runtime and plan migration before .NET 8 support ends on 10 November 2026. |
| Publisher trust | 0.6.0 continues the repository's existing unsigned distribution model, disclosed in the installer documentation and release notes. Checksums and build attestations are configured. | Establish signing and verify the download/SmartScreen experience before claiming broad end-user production readiness. |
| Release identity | The project, agent manifests, installation example, changelog and release notes now identify 0.6.0. | Tag the tested commit and publish its CI-built artifacts, then update Scoop/winget with the published checksum. |

Microsoft lists .NET 8.0.31 as the current patch and 10 November 2026 as the end of support in its
[support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), checked on 22 September
2026. A later release must recheck this rather than treating the pinned patch as permanently current.

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process) and the
[live verification instructions](DEVELOPMENT-TESTING.md#reproducible-live-verification). Test the
candidate daemon and host together, not a new CLI against an older running daemon. The candidate
is running locally and the live fixtures cleaned up their own apps and lease. Local reports are in
`artifacts/release-0.6-desktop` and `artifacts/release-0.6-browser`; they contain workstation screenshots
and are not published. No automated real-pointer movement or visible/control isolation test was run.
The final package must be retested after code changes.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
