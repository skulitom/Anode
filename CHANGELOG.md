# Changelog

## Unreleased

- Reject duplicate JSON fields and invalid Unicode escapes before any action, including nested
  tool arguments, and keep MCP and named-pipe connections usable after malformed requests.
- Execution requests cancelled before dispatch no longer start or cancel jobs. Already started
  jobs remain recoverable after a client cancellation or disconnect.
- Command output always decodes as UTF-8 and preserves whole Unicode characters through stream
  buffering, pagination and history truncation. `maxChars` counts Unicode code points.
- Rebuilt the mark as an amber app tile with a symmetric diagonal cursor and generate all icon sizes from the source SVG, with a
  light/dark size proof for asset review. Small icons preserve a complete frame at tray sizes,
  and the release package includes the SVG referenced by its README.
- Viewer polish: consistent dark hover/checked states, Windows high-contrast colors, an explicit
  input-mode label, readable connection states and expandable, copyable status details. Emergency
  Stop, control release and Exit full screen remain visible in full screen and outside overflow.
  Details report when another application owns the emergency shortcut.
- The installer detects Windows through the runtime, so installation works from agent runners
  that omit the optional `OS` environment variable.
- Viewer connections and reconnects now refuse to proceed if pointer isolation cannot be installed.
  A partially written guard no longer reports itself as installed.
- Self-contained builds explicitly use .NET 8.0.31 instead of inheriting an older runtime from
  the developer's installed SDK. Release checks reject a nonempty Unreleased changelog section.
- MCP server instructions and the `anode-desktop` skill now lead with the lease workflow:
  `seat_status`, `seat_lease` acquisition, `seat_capabilities`, the work, cleanup, release.
- **Breaking:** only `seat_start` and `seat_lease` acquisition start a seat over MCP.
  `seat_capabilities`, `seat_processes` and `steam_status` report a stopped seat instead of
  starting one, and a stale lease token no longer relaunches a daemon the user quit. When a person
  stops the seat or quits Anode, diagnostics report a stopped seat and desktop tools ask for a new
  lease instead of returning pipe errors. `steam_status` adds `seatSession` and `runningOutsideSeat`.
- Observation tools are annotated read-only and `seat_start`/`seat_hide` additive, so clients that
  honor annotations need not ask for approval on every observation. Claude Code asks before every
  `seat_stop`, even when Anode's tools are allowlisted. Every tool has an explicit title, and input
  schemas list enum values and ranges that match validation; unlisted values such as screenshot
  format `jpg` are now rejected instead of passing through.
- The MCP `initialize` result carries a description, and a blank `ANODE_AGENT_ID` counts as unset.
- Results with an image block no longer repeat the control tree in `structuredContent`, which made
  Codex drop `seat_observe` screenshots and Claude Code receive every tree twice. Text summaries
  now carry automation IDs and bounds.
- Added the MCP prompts `desktop_test` and `desktop_guide`, which clients such as Claude Code and
  VS Code list as slash commands.
- **Breaking:** the CLI rejects unknown options and malformed input with exit code 2 before
  anything runs. `<command> --help` never runs the command, and `anode help <command>` describes
  one command. `doctor` names the next step and accepts `--json`; `status --json` prints a stopped
  result when Anode is not running.
  Double-clicking `anode.exe` explains how to run it from a terminal instead of doing nothing, and
  `anode quit` returns once Anode has exited, so a following `anode start` applies its options.
- Anode has an icon (`assets/anode.svg`, `anode.ico`), used by the executable, viewer and tray, and
  a repository social preview image. The executable's file properties describe Anode.
- Viewer and tray: **Sign in…** and **Help** in the tray menu, and startup errors that name their
  remedy and troubleshooting section.
- The installer hides the slow Windows PowerShell progress bar, reports download sizes and prints
  next steps; `anode configure` validates the Claude Code settings before changing anything and
  says how to verify the registration.
- Release archives use `/` entry names and LF checksums, releases carry build-provenance
  attestations, and only the highest version is marked Latest. `build.ps1` refuses to overwrite an
  `anode.exe` that is in use. `scripts/test-distribution.ps1` and `scripts/check-docs.ps1` check
  versions, checksums, manifests, tool counts and documentation links in CI.
- Distribution: a Claude Code plugin marketplace (`/plugin marketplace add skulitom/Anode`, then
  `/plugin install anode@anode`) and a Scoop bucket
  (`scoop bucket add anode https://github.com/skulitom/Anode`, then `scoop install anode/anode`).
  MCP Registry, winget and MCP Bundle metadata are prepared but not yet published.
- Documentation: Claude Desktop, VS Code and Cursor setup, a CLI command reference, desktop lease
  troubleshooting and a privacy statement.

## 0.5.0 — 2026-09-21

- Added **Sign in…** beside **Reconnect** in the viewer header to request Windows credentials
  from a running Anode instance, without signing out the seat or closing its programs.
- Fixed the viewer moving the real mouse pointer. When a program in the seat called `SetCursorPos`
  (SDL games do on every switch between relative and absolute mouse mode), the Remote Desktop control
  applied the server's pointer-position update to the user's desktop whenever the real pointer was
  over the viewer's rectangle, even with the viewer view-only, unfocused or hidden. Measured: 5 of 5
  seat cursor moves teleported the real pointer within 2 ms. The daemon now gates `mstscax.dll`'s
  `SetCursorPos` import inside its own process: moves reach the real pointer only while the viewer
  is visible, focused and control is taken. `anode status` reports `pointerGuard`; a quick check
  exercises the patched import and the opt-in `scripts/test-pointer-isolation.ps1` proves it against
  a live seat. Restart an existing daemon to load the guard. See the
  [investigation record](https://github.com/skulitom/Anode/blob/main/bugreports/2026-09-18-viewer-moves-the-real-pointer.md).
- Added `seat_lease` / `anode lease` for exclusive desktop acquisition, renewal and release.
  Leases expire without renewal and reject stale queued actions; in-flight operations finish
  before ownership can transfer. Stop remains immediate and global.
- Added per-agent MCP identities, stable-ID recovery, owner-scoped command jobs and optional
  job cancellation on release. Release/expiry clear desktop references and held input/controllers.
- **Breaking:** desktop tools now require a lease. CLI scripts must run `anode lease acquire`
  and pass `ANODE_AGENT_ID`/`ANODE_LEASE_TOKEN`; MCP supplies them automatically after `seat_lease`
  acquisition. Updated guides and private-pipe regression checks. Quit Anode before updating so
  the daemon and seat host load the new protocol.
- Explicitly publish the connector script and portable skill beside the executable so clean
  and incremental package builds include the files required by installation/configuration.

## 0.4.0 — 2026-09-15

- Added a discoverable `anode-desktop` Agent Skill, installed with client configuration,
  to prefer Anode for native GUI work and headed testing while preserving direct tools
  for tasks that do not need a desktop. Added an MCP-only opt-out and protected skill edits.
- Added `anode_guide` and `anode guide [--json]`, sharing an embedded workflow that works
  before setup and remains responsive during long tools.
- Made MCP `seat_status` a read-only check that never creates a daemon or seat. Call
  `seat_start` when desktop work is needed. Added tool titles, conservative effect
  annotations, explicit auto-start descriptions and scoped cleanup guidance.
- Added an agent-facing guide and `llms.txt` index, with discovery and skill installation
  checks in the quick test and release installation suites.

## 0.3.1 — 2026-09-15

- Added downloadable Windows x64 release bundles with docs, installer and SHA-256 sums.
- Added a per-user PowerShell installer with offline/version selection, optional client
  registration, PATH setup, locked-file checks and rollback of failed file updates.
- Added `anode configure` with automatic Codex/Claude Code CLI detection and preflight
  checks, using the existing backed-up configuration and tool timeout handling.
- Added release automation and disposable installation/configuration checks in Windows CI.
- Reorganized the README around downloads and first use; added installation, update,
  removal and contribution guides plus structured issue forms.

## 0.3.0 — 2026-09-14

- Added command jobs with separate stdout/stderr, exit codes, incremental cursors,
  cancellation, hard lifetimes and cleanup of owned descendants.
- Added condition waits with fresh actionable observations, measured capture/input
  diagnostics and 30 MCP tools. Added a window raise action and reliable focus from
  disposable workers, with explicit errors when Windows refuses focus.
- Removed the initial viewer flash from hidden startup. Verified sustained hidden
  capture after activating the per-user rendering preference.
- Identified a SYSTEM GameInput helper blocking focus and synthetic input. Added a
  precise diagnostic, refusal of ineffective input and an opt-in administrator
  repair that stops only the affected child-session helper.
- Added repeatable Codex/Claude CLI registration with config backups and timeouts,
  a development/testing guide and Claude repository instructions.
- Expanded verification to 38 quick checks, 15 native fixture checks, and a browser/
  command suite covering headed Chrome, form/HTTP behavior, mobile layout, capture,
  actual Anode mouse/keyboard input, working directories, environment and exit codes.

Live checks preserved the hidden viewer, main Steam client, boxed Steam client and
Liftoff. The one-time GameInput repair restored input; the Windows service may recreate
its helper, so this is not a permanent service configuration change. Gamepad gameplay,
the vision pilot and application types beyond the recorded fixtures remain unverified.

## 0.2.0 — 2026-09-14

- Added window discovery, UI Automation control trees, bounded text extraction and
  direct window/control actions through CLI and MCP (26 tools in total).
- Added compact text summaries, structured MCP results and searchable standalone
  HTML inspection reports with optional screenshots and control bounds.
- Kept accessibility in verified child-session workers with deadlines, expiring
  references, password-value omission and no replay of uncertain actions.
- Made automatic CLI/MCP daemon startup hide the viewer. Added a reversible
  per-user RDP rendering preference and hide-on-minimize behavior. Hidden capture
  still requires validation on the reported machine after a daemon restart.
- Fixed listener readiness, service restart when setup requires it, detached-daemon
  log paths, actionable startup errors, RDP interop and child-session verification.
- Added prompt-free unattended failure handling and explicit `--sign-in` support.
- Hardened pipe deadlines, cancellation, access control and MCP scheduling so Stop
  remains available while ordinary tool work is busy.
- Guarded direct Steam launches when a client is running outside the seat.
- Added 33 quick checks, a 13-check opt-in desktop fixture, Windows CI, investigation
  records and expanded setup, troubleshooting and agent documentation.

Live validation covers control automation with a hidden viewer. Liftoff launch in
an isolated Steam client was observed; gameplay and the vision pilot remain unverified.
