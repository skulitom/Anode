# Changelog

## Unreleased

- **Virtual controllers stay in the seat.** A ViGEm pad is a device on the machine, so a game on your
  desktop read the input an agent sent to a game in the seat: an agent flying Liftoff steered No Man's
  Sky. With [HidHide](https://github.com/nefarius/HidHide/releases) installed, the seat lists every
  virtual pad plugged in while it runs with HidHide, jailed to the seat's session. Programs in the seat
  open it as usual; games, Steam and the Xbox Game Bar on your desktop are refused, so you can play
  while agents test games. This covers pads a program in the seat plugs in on its own. While a
  program outside the seat uses ViGEm, such as DS4Windows, only Anode's own pads are kept in the seat.
- The names a pad takes are listed before it plugs in, because HidHide checks each open, not handles
  already open. After plugging a controller in, the seat asks the daemon to open it from your session;
  if it can, the controller is unplugged again and `gamepad_attach` fails with `not_isolated`, as it
  does when HidHide is switched off with other devices listed or its application list is inverted.
- `gamepad_attach` reports `seatOnly` and the pad's device; `anode gamepad state` reports each pad and
  HidHide's state. Anode changes only its own HidHide entries, removes them when a seat ends or at the
  next start, and switches HidHide on only when nothing else is listed. `anode doctor` checks for
  HidHide. Without it, controllers are machine-wide as before, and each attach says so.
- The seat log records each controller plugged in or unplugged.

## 0.9.0 — 2026-09-24

- **.NET 10.** Anode moves to .NET 10, the current long-term support release, before .NET 8 support
  ends on 10 November 2026. Release builds bundle runtime 10.0.12 and still need no .NET
  installation; building from source needs the .NET 10 SDK. The viewer renders as it did on .NET 8.
- **Steam stays on your desktop.** `anode steam <appid>` and `steam_launch` start the game itself in
  the seat and connect it to the Steam client already running on your desktop, instead of starting
  a Steam client in the seat that took Steam over from you. When Steam is not running, it starts on
  your desktop, minimized. Before starting the game Anode asks Steam, the way a game does, whether an
  account is signed in and connected, because Steam's own records claim one too early. The program
  comes from Steam's launch configuration; `--exe` (MCP: `exe`) names another. Such games run without
  the Steam overlay or Steam Input. `--force` keeps the old launch through a client in the seat, and a
  client already in the seat is used as before.
- `steam status` and `steam_status` report which session the Steam client runs in and whether it is
  signed in (`clientSession`, `signedIn`). Quick checks cover launch configurations, the launch
  paths and the namespace links without starting Steam.
- **Agents can tell who has the desktop.** Every session of one client used to share its name, so
  an agent holding the desktop read "Desktop in use by Claude Code" and took it for another Claude
  Code session. MCP now names an agent after its client and project folder, such as "Claude Code in
  WebShop", `seat_status` tells the owner "You hold the desktop lease", and the seat's log
  records each time the desktop is taken, released or expires. `ANODE_AGENT_NAME` still overrides.
- **A separate dev Anode.** Debug builds are the `dev` channel, and any build joins a channel with
  `ANODE_CHANNEL` or `--channel NAME` before the command. A channel has its own daemon, pipes, logs
  (`%LOCALAPPDATA%\Anode-dev`) and a viewer and tray icon labelled "(dev)", so a development build
  cannot reach, stop or quit the installed Anode. The installed Anode keeps every name it had.
- Windows gives a Windows session one child session, so one channel at a time runs a seat. A dev
  Anode will not start a seat while the installed Anode's runs, including Anode 0.8.0 and earlier,
  and the installed Anode will not start one while a dev seat runs; `start`, `lease acquire`,
  `seat_start` and the Start menu say which Anode has the seat instead of taking it over.
- `anode status` from a dev build names its channel and says when another Anode has the seat; the
  daemon's status reports `channel`. `anode configure` refuses on a dev channel, since it would
  replace the clients' `anode` entry.
- Quick checks cover channel names and the seat hand-off with a private mutex and made-up pipe
  lists, never the real seat.
- The security and troubleshooting guides and the `anode-desktop` skill warn that apps which restore
  a session, such as Windows 11 Notepad, reopen your own documents in the seat, unsaved ones included.

## 0.8.0 — 2026-09-22

- **Agents take turns without bookkeeping.** When another agent has the desktop, `seat_lease`
  acquire waits in a first-come line (`waitSeconds`, 0-300, default 30 over MCP; `anode lease
  acquire --wait`) instead of failing at once. A place is kept only while its agent keeps asking,
  so the desktop never goes to an agent that gave up or went away.
- Each desktop action extends its lease to a full lifetime from when it starts, so a working agent
  no longer renews; `renew` covers long pauses. An expired lease is still never revived.
- MCP desktop tools take the lease themselves when the desktop is free, without ever starting a
  seat. Pointer, keyboard and gamepad input that arrives without a lease is refused once with a
  request to observe first, because it was aimed at a screen another agent may have changed.
- Ending an MCP session whose identity Anode generated releases its lease at once instead of
  leaving others to wait for expiry; a stable `ANODE_AGENT_ID` still keeps it for recovery.
- Lease status, `seat_status`, `anode status` and the viewer's footer name the owner, from the MCP
  client (such as Claude Code or Codex) or `ANODE_AGENT_NAME`, and list the agents waiting. While
  others wait, desktop results say so, so the owner can hand the desktop on.

## 0.7.0 — 2026-09-22

- The installer adds an **Anode** shortcut to the Start menu, so Windows Search finds Anode.
  `-NoShortcut` skips it; updates refresh it, and installer checks never touch the real Start menu.
  When the installer runs inside a packaged app, such as an agent's desktop app, and Windows keeps
  the shortcut or installation private to that app, it says so instead of reporting success.
- Anode appears in **Settings → Apps → Installed apps**, and **Uninstall** there runs the new
  `uninstall.ps1`: it offers to quit a running Anode, removes the installed files (keeping files you
  added), the PATH entry, Start menu shortcut and Installed apps entry, and Codex and Claude Code
  registrations that run this installation, with their unmodified skill. Undoing machine setup is
  offered only while no Anode runs. `connect-agents.ps1 -Remove` performs the unregistration alone.
- Opening Anode from the Start menu or double-clicking `anode.exe` now brings the viewer to the
  front, starting Anode or a stopped seat first, instead of explaining how to use a terminal. When
  machine setup is missing, a dialog offers to run it with one administrator prompt; problems setup
  cannot fix, such as Windows Home, link to the requirements.
- A calmer, more modern viewer. The title bar, header, details and footer are one dark surface:
  the title bar is dark and, on Windows 11, matches the header, with no divider lines, separators or
  sizing grip. Hover, pressed and checked states are rounded fills instead of orange outlines,
  **Stop seat** is always a tinted button, the seat state carries a colored dot that stays visible
  at the minimum width, and details show a scroll bar only when they overflow. The tray menu is dark
  with rounded rows and no icon margin. Labels, control names, shortcuts and full-screen safety
  controls are unchanged, and high-contrast mode keeps the system look.
- Header buttons carry icons (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10)
  that follow each button's state, and the overflow menu opens from a "more" icon.
- While Remote Desktop is not connected, the viewer shows a dark screen with the seat's state, its
  message and a progress indicator instead of the control's blank window. It gives way the moment
  the control connects, so a live seat, including a Windows sign-in screen, is never covered.
- Contributors can render the viewer and tray menu to PNGs with `anode __viewer-preview`, without
  a seat or a visible window.

## 0.6.0 — 2026-09-22

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
