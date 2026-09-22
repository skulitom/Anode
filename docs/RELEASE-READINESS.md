# Release readiness — 22 September 2026

**Decision: not ready for a broad production release.** The current tree is a release candidate
under development. Local checks provide useful evidence, but do not certify live desktop isolation
or compatibility across supported Windows versions and agent clients.

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

| Area | Evidence | What remains before release |
| --- | --- | --- |
| Build and protocol | Debug/Release builds and private-pipe quick checks cover MCP discovery, argument validation, ordering, cancellation, Stop, leases, ownership and session identity. | Run CI against the final release commit and retain its results. |
| Packaging and installation | Disposable installation, update/rollback and client-configuration tests exercise local packages without modifying real client settings. | Install/upgrade the final package on a clean supported Windows machine; exercise an actual client, not only the fake client CLIs. |
| Native desktop and browser input | Live suites exist; earlier investigation records describe tests of older builds on this workstation. | Run `test-desktop.ps1` and `test-development.ps1 -VerifyInput` against the exact candidate in an owned seat. Verify hidden screenshots, delivered input, cancellation, reconnect and sign-in recovery. |
| Pointer isolation | Quick tests exercise the guard through the real RDP client's import slot without a connection. | Run `test-pointer-isolation.ps1` against the candidate. Require actual suppressed RDP moves; a run with no suppression is inconclusive. Visible/control phases require a coordinated interactive step. |
| Windows and accessibility | Hidden rendering covers the viewer layout; the dark text palette exceeds 4.5:1 contrast. | Check keyboard and screen-reader navigation, high contrast and 100–200% DPI on supported Windows configurations. |
| Runtime servicing | The project pins the currently published .NET 8 patch. | Keep servicing the bundled runtime and plan migration before .NET 8 support ends on 10 November 2026. |
| Publisher trust | The local executable reports `NotSigned`; release docs disclose unsigned binaries. Checksums and build attestations are configured. | For a broad end-user release, establish signing and verify the download/SmartScreen experience. Unsigned beta distribution needs an explicit, documented decision. |
| Release identity | The project and release notes still say 0.5.0; tag `v0.5.0` already exists, and new behavior is under Unreleased. | Choose the next version, assign its changelog, update release notes/manifests, and create the release from that tested commit. |

Microsoft lists .NET 8.0.31 as the current patch and 10 November 2026 as the end of support in its
[support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), checked on 22 September
2026. A later release must recheck this rather than treating the pinned patch as permanently current.

## How to finish validation

Follow the [release process](../CONTRIBUTING.md#release-process) and the
[live verification instructions](DEVELOPMENT-TESTING.md#reproducible-live-verification). Test the
candidate daemon and host together, not a new CLI against an older running daemon. This review did
not stop or replace the user's running Anode instance, move their pointer or show the live viewer.
The final package must be retested after any further changes.

Anode remains desktop isolation for trusted agents running as the user, not a security sandbox.
The [security model](SECURITY.md) and known application/Windows limitations still apply. No claim of
a comprehensive security audit, Windows compatibility certification or long-running soak test is
made by this review.
