# Privacy

Anode is local software. It has no accounts, and it sends nothing to its maintainers. This page
covers what it reads, where that data goes and what it keeps on your machine.

## What Anode sends over the network

Nothing, at run time. The CLI, MCP server, daemon and seat host have no telemetry, analytics,
crash reporting, update check or remote control endpoint. The seat is a loopback Remote Desktop
connection to your own machine. Anode's named pipes are local and accept only processes running
as the same Windows user.

Only the installer uses the network: `install.ps1` downloads the release archive and its
`SHA256SUMS` from this repository's GitHub releases, and
[offline installation](INSTALL.md#portable-or-offline) is available. `anode configure` makes no
network requests itself; it runs the installed Codex or Claude Code CLI to register Anode, and
those clients follow their own policies.

Programs that you or an agent start in the seat use the network as they normally would. They run
as your Windows user; see [what the seat is not isolated from](SECURITY.md#what-the-seat-is-not-isolated-from).

## What your agent receives

On request, Anode returns screenshots, window titles, accessible names and values (UI text),
process lists and command-job output to the MCP client or CLI that asked. Apart from leaving out
password-field values, Anode does not filter this content. The MCP client may send it to its
model provider under that provider's terms. Anode cannot see or control what the client does
with it. Keep content the provider should not receive out of the seat, and remember that the
seat can open anything your account can.

## What stays on your machine

All of this lives in `%LOCALAPPDATA%\Anode`, or the folder you pass with `--state-dir`:

- `anode.log`: timestamps, roles, process and session IDs, launched program paths, Steam app IDs,
  who took, released or let expire the desktop lease (agent names, which include the agent's
  project folder name, and the start of agent IDs), seat stop reasons, startup checks and errors.
  Command-job requests, argument values and environment overrides are not logged.
- `rdp-rendering-backup.json`: your previous per-user RDP rendering preference, so
  `anode rendering --restore` can put it back.
- `browser-sign-in.html`: the page `anode browser --sign-in` last showed, with the seat profile's
  folder and the addresses you gave it.
- `android\`: one log per Android emulator the seat started, named after its serial, for example
  `emulator-5554.log`. It holds the emulator's own output: the start time, the emulator's path and
  arguments (the AVD name and its options), and its boot, adb and error messages.

The seat also keeps profiles of its own in `%LOCALAPPDATA%`, apart from your own browser and
Android Studio. `--state-dir` does not move them:

- `AnodeChrome` and `AnodeEdge`: the seat's browser profiles. They hold what Chrome or Edge keeps
  for any profile, including the sign-ins of the sites you signed in to there, cookies, history and
  cache, and any password you chose to save. Agents use the sites signed in there, and the pages
  they open add to the history and cache.
- `AnodeAndroidStudio`: Android Studio's settings, caches, plugins and logs for Studio in the seat,
  including any account you signed in to there.

`anode configure` also leaves timestamped backups beside the client settings it changes.
Command-job output is held only in the seat host's memory. Screenshots and inspection reports are
written to disk only when you ask for a file, for example `anode shot seat.png` or
`anode inspect <windowId> --html report.html`.

## Retention and removal

Anode never uploads or rotates its log; it grows until you delete it. Each emulator log is
replaced the next time an emulator starts with the same serial. Command-job output is discarded
when the seat host exits or its bounded history evicts the job. The seat profiles stay, signed in,
between seats and after Anode is removed, until you delete them. To sign a site out, open the
profile with `anode browser --sign-in <the site's address>` (add `--edge` for Edge's) and sign out
there. To remove everything, follow
[Remove](INSTALL.md#remove), then delete `%LOCALAPPDATA%\Anode` and, with no seat browser or Android
Studio open, `%LOCALAPPDATA%\AnodeChrome`, `AnodeEdge` and `AnodeAndroidStudio`.

## Contact

Ask questions or report a privacy problem through
[GitHub issues](https://github.com/skulitom/Anode/issues). For a security problem, follow
[reporting a problem](SECURITY.md#reporting-a-problem).
