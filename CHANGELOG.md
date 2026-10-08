# Changelog

## Unreleased

- **`set_value` checks that the value took.** On a Chrome drop-down list (`<select>`), `seat_element`
  `set_value` reported `performed` while the list kept its old choice, so an agent could submit a
  form with the wrong option. Anode now reads the control back: if it still shows its old value 1.5
  seconds later, the action fails and says to expand the list and select the option. A control that
  reformats the value succeeds and returns what it now reads.
- **`seat_observe` lists each control once.** While a Chrome drop-down list was open, `seat_observe`
  returned the window's tree nested inside itself, over and over, until its element budget ran out.
  An app can list one control in two places, or even inside itself. The walk now skips a control with
  the same UI Automation runtime ID as one it has already listed, without using the budget, and
  `skippedRepeats` says how many it skipped.
- **Packaged apps' Anode folders.** A daemon started by a desktop app that redirects AppData, such
  as the Claude desktop app, keeps its log and state in that app's
  `%LOCALAPPDATA%\Packages\<app>\LocalCache\Local\Anode`, which the privacy, install and troubleshooting
  pages now say. `anode rendering --restore`, which the uninstaller runs, looked only in
  `%LOCALAPPDATA%\Anode` and reported no backup. Without `--state-dir` it now also restores a backup
  saved there, and lists them with their values, changing nothing, when several apps saved one.
  Inside such an app it refuses and asks for your own terminal.
- **Faster desktop startup.** CLI and MCP skip their initial connection wait when no daemon is
  present, while still waiting for one that is starting. Reconnects retain their existing host
  reuse window.
- **Quicker startup feedback.** Readiness, failure and Stop wake waiting startup calls immediately.
  MCP connects as soon as it launches the daemon, and failed host connections retry sooner.
- **Queued requests keep their deadline.** A request that waited behind another long operation, such as
  typing, got a full 60 seconds of its own once it reached the seat. So it could still be running when
  the daemon gave up on it: the reply was lost and the seat showed as `detached` until it was
  reconnected. The daemon now forwards the time a request has left, the seat takes its own waits off as
  well, and a desktop operation is stopped a second before its time runs out, so one queued behind
  another answers, or fails, in time.
- **Typing keeps pace with the program.** `seat_type` and `anode type` sent characters as fast as
  Windows took them, so a busy program, such as Chrome editing a long field, could drop and reorder
  them while the call still reported success. Characters now go out 15 ms apart by default, timed
  precisely, and typing pauses while the focused program is busy; `perCharMs` sets the pace. Text
  that can't be typed within the request's time is refused before typing, and a program that stops
  responding, a slow one that runs out the request's time, or input Windows refuses partway ends the
  call with the exact count typed.
- **The sign-in window explains itself.** `anode browser --sign-in` opened a blank window when given
  no address, with its guidance in the terminal behind it. Its first tab now says what the window is
  for and that it must be closed afterwards, and up to 8 addresses open in tabs of their own.
  `seat_browser` and the agent guide hand the user the whole command, address included.
- **A seat profile open on the other desktop is reported.** `seat_browser` and `anode browser
  --sign-in` reported a window while the other desktop had the seat profile open, though a browser
  started there exits without one. Both now refuse, name the browser process that holds the profile's
  lock, found through Restart Manager, and say how to close it, including a Chrome left running in the
  background with no window.
- **Newer control types no longer break observation.** `seat_observe` and `seat_wait` failed on File
  Explorer windows with "Object reference not set to an instance of an object": .NET's UI Automation
  client has no name for the AppBar control type of Explorer's command bar, or for SemanticZoom. Such
  controls now have the role `Unknown`, and `seat_element` acts on them as on any other control. A text
  control that provides no document range or selection is read without them.
- **The uninstaller lists what it leaves.** After removing `anode.exe`, it pointed at `anode setup
  --undo` to turn child sessions off, and named only the logs. It now ends with what really stays on
  the machine, each with how to remove it without Anode: logs and settings (a packaged app's too), a
  Remote Desktop rendering preference not yet restored, the seat's browser and Android Studio
  profiles, still signed in, the copies of agent client settings saved before Anode changed them,
  child sessions, the Remote Desktop host, and the `--fps 60` and `--gpu` values. It says when
  another Anode on the machine shares them. The security page shows how to turn child sessions off
  and restore the rendering preference without `anode.exe`, and a rendering restore that fails
  during removal is reported.

## 0.11.1 — 2026-10-02

- **Safer adb commands.** The seat refuses shared-server commands, including those behind repeated
  `wait-for-*` prefixes, and refuses to replace or remove another device's forwarded local port.
  Emulator startup keeps boot probes inside the caller's reply deadline, including when adb hangs.
- **Display changes are remembered only when confirmed.** The viewer keeps the size Windows
  actually applied. A lost reply is never replayed or recorded as a successful change, and lease
  cleanup still restores the startup display if the original request finishes late.
- **Killed agents release the desktop.** A generated MCP identity tracks its process and start time;
  its lease ends after the process exits and any admitted action finishes. A stable `ANODE_AGENT_ID`
  keeps its existing release-or-expiry behavior.
- **Audio works with older MCP clients.** Protocol `2024-11-05` receives WAV recordings as embedded
  resources; newer clients continue to receive native audio blocks.
- **Tested live on Windows 11.** Native desktop controls, actual Chrome mouse/keyboard delivery,
  four display changes and reset, audio playback/recording, Android boot/capture/stop, hidden-viewer
  pointer isolation and a seat-only Xbox controller passed. A killed MCP client released its lease
  in 406 ms and restored the display before the next client acquired it. Both old and new MCP audio
  formats passed against the running seat. See [release validation](docs/RELEASE-READINESS.md).

- **Ready for directory submissions.** The README leads with desktop isolation and a three-command
  install, keeps a hidden demo GIF placeholder, and includes the MCP Registry ownership marker.
- `publish-registry.yml` publishes to the official MCP Registry using GitHub OIDC, with no secret
  or registry sign-in: a first metadata-only listing on merge, then a per-release MCPB entry once the
  tested bundle is attached. Runs are idempotent. The repository's `server.json` stays metadata-only;
  [the publishing guide](docs/PUBLISHING.md) covers the Claude Desktop test gate and manual directories.
- The Release workflow builds and attests the MCPB bundle and uploads it, its checksum and registry
  entry as a workflow artifact for owner testing. Public release assets remain the ZIP, installer and
  `SHA256SUMS`. CI exercises the generator, validates registry metadata with a pinned publisher, and
  checks the generated entry against the bundle.

## 0.11.0 — 2026-09-29

- **Android apps in the seat.** `android_emulator` (`anode android start|adb|shot|stop`) boots an AVD on
  an emulator inside the seat, on a free port, and returns its serial once it has booted. It starts
  read-only, so tests leave the AVD as it was and can run while the same AVD is open on the desktop.
  `adb` runs for that serial only and refuses commands for adb's server or other devices, and
  `screenshot` returns the device's own pixels. Every Android action checks that its emulator's
  process runs in the seat, so the user's own emulators and phones, which share the adb server, stay
  out of reach. `gpu` picks auto, host or software rendering, naming software as the installed
  emulator does. `android_status` (`anode android`) lists the SDK, AVDs, running emulators and Android
  Studio without starting anything, and the `android_test` prompt walks a release test.
- **Android Studio in the seat.** `android_studio` (`anode android studio`) opens it on a profile of its
  own, with its setup wizard skipped. Android Studio hands a second start on the same settings to the
  instance already running, in any session, so on the user's settings it opened projects on the user's
  desktop; `run` now refuses its launchers. Remains of an uninstalled Studio are skipped.
- **Signed-in web consoles.** `seat_browser` (`anode browser <url>`) opens Chrome or Edge in the seat on
  a persistent seat profile, `%LOCALAPPDATA%\AnodeChrome` or `AnodeEdge`. `anode browser --sign-in`
  opens that profile on the user's own desktop to sign in once, and refuses a terminal inside a
  packaged app, which would keep the sign-in where the seat cannot see it. `run` refuses Chrome and
  Edge on their usual profile, and links they would open, while they run outside the seat, where they
  keep that profile locked. Anyone holding the lease can use the sites signed in there.
- **Tested live** on Windows 11 Pro build 26200 with emulator 36.2.12: `Pixel_9_Pro` (API 36, Google
  Play) booted in the seat from its quick-boot snapshot in 12.2 s on the graphics card, adb reported
  its 1280x2856 screen, a screenshot came back at that size, `adb kill-server` was refused, and it shut
  down on `emu kill`. Android Studio opened at its Welcome screen on its own profile, with no setup
  wizard, and Chrome opened a page in the seat on the seat profile, while `run` refused Chrome and a
  link as long as Chrome ran on the desktop.
- [Android](docs/ANDROID.md#when-an-emulator-does-not-finish-booting) explains an emulator that never
  finishes booting: a quick-boot snapshot that resumes with adb offline, and an AVD that restarts
  during every boot because of a fault in its own disk image, which the kernel log the emulator keeps
  names.
- **Agents can test other displays.** The new `seat_display` tool and `anode display` command change
  the running seat's resolution and Windows scaling while its apps keep running: 640x480 to 8192x8192,
  portrait included, at 100-500%. Anode asks Windows for the change live through Remote Desktop's
  display-control channel and measures the seat's screen until it shows it; when Windows does not
  change it live, Anode reconnects its viewer at the new size, which keeps the session and its apps.
  Results report the display Windows actually applied. Changing the display used to mean quitting
  Anode, which closed every app in the seat, and MCP agents could not do it at all. A seat that signed
  in through the Windows credential dialog is only changed live: reconnecting would need the password,
  and Anode never opens that dialog for an agent.
- A display change lasts while its agent holds the desktop lease; release or expiry restores the
  display the seat started with before the next agent acts. `reset` restores it at once.
- The `display_test` MCP prompt runs an app through a set of displays, from 1024x768 to 4K at 200% and
  portrait, and asks for a report of what breaks at each.
- Screenshots, input and control bounds now use physical pixels at every scaling. The seat host and
  its inspection worker were not DPI aware, so in a seat started with `--scale` above 100 Windows gave
  them a smaller, DPI-scaled screen, and screenshots were not at the display's real resolution.
- `anode status`, `seat_status`, `seat_capabilities` and the seat host's `ping` report the display's
  scaling, and status reports `startupDisplay`. `anode start` with display options says to use
  `anode display` when Anode is already running, instead of ignoring them silently. `--scale 100`
  now asks for 100% instead of leaving the scale to Remote Desktop's default.
- **Tested live** in a seat that signed in through the Windows credential dialog: Windows applied
  every change live, in 156-173 ms, through 1024x768, 1920x1080 at 150%, 1080x1920 portrait and
  2560x1440 at 125%, with screenshots at each display's own size, and `reset` restored 1280x720 at
  100%. The desktop suite passed at 1920x1080 and 150%, with the pointer at physical coordinates. The
  reconnect fallback has not run live, because nothing needed it.

- Agents can listen to browser/game audio as WAV clips and play bounded PCM WAV files through
  new audio MCP tools and `anode audio` commands. Audio uses the verified child session's Remote
  Audio endpoint, requires `--audio` at startup (also audible on the user's speakers), and never
  falls back to a physical capture device. Agent playback stops when its desktop lease ends.
- **Audio is not tested live yet.** Builds, quick self-tests and documentation checks pass, but
  playback, recording and browser/game audio have not run in a seat: the 0.11.0 validation seat
  started without `--audio`.

## 0.10.0 — 2026-09-25

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
- **The viewer opens fitted to the seat.** Its size was estimated, so the seat was scaled slightly and
  white strips showed at its sides. The viewer now measures its frame, toolbar and status bar and
  opens with the seat at its own size (1:1 at 100% scaling), or the largest that fits the screen. A
  resized, maximized or full-screen viewer keeps the seat's shape, centred on the viewer's dark
  background instead of the Remote Desktop control's white bars.

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
