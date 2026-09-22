# Using Anode

Start with [installation](INSTALL.md) and [agent configuration](CONNECTING-AGENTS.md). Every
command and option is listed in the [command reference](#command-reference).

> **Before you start:** desktop commands need a [desktop lease](MULTI-AGENT.md#cli-workflow). Set
> `ANODE_AGENT_ID`, run `anode lease acquire`, keep the returned token in `ANODE_LEASE_TOKEN`,
> renew before expiry and release when finished. Job reads and cancellation need only the original
> agent ID. MCP agents call `seat_lease` instead; the server keeps the token for them.

## Develop and test applications

Run builds, test runners and local servers with `exec`, then read incremental output with `job`.
Use `run` for an ordinary GUI application that should remain open. Inspect controls with
`windows` and `inspect`; `wait` handles delayed UI states without guessing a fixed sleep.
Inspection can produce a standalone HTML report with a screenshot and searchable control tree.

```powershell
anode exec --cwd C:\project --timeout 600000 -- dotnet build | Out-Host
anode jobs | Out-Host
anode job j_RETURNED_ID --wait 1000 --json | Out-Host
anode inspect w_RETURNED_ID --html inspection.html | Out-Host
anode wait w_RETURNED_ID --automation-id SaveButton --state enabled | Out-Host
```

From a source checkout, opt-in live checks use an existing seat and clean up their own fixtures.
They renew the lease you already hold; they never acquire one or start a seat themselves:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-desktop.ps1
powershell -ExecutionPolicy Bypass -File scripts\test-development.ps1 -InstallBrowserTools
```

The second script needs Node.js/npm and Chrome; it installs pinned Playwright locally under
ignored artifacts. It tests a headed browser in the seat, HTTP requests, form input, screenshots,
mobile layout and command output. App-specific accessibility, elevation and singleton behavior
still need verification. No tool redirects an unrelated foreground computer-use service into Anode.

---

## Setup, once per machine

```powershell
anode setup                 # the two settings a seat needs
anode setup --fps 60 --gpu  # and the two that make games usable
```

You will see one UAC prompt. Decline it and nothing changes. Exactly what it does, and nothing
else, is listed in [docs/SECURITY.md](SECURITY.md):

| Setting | Value | Why |
| --- | --- | --- |
| `fDenyTSConnections` | `0` | The machine needs a Remote Desktop host for a loopback session to connect to. **No firewall rule is added**; network access depends on your existing firewall rules. |
| `WTSEnableChildSessions` | `TRUE` | The documented switch for the feature. |
| `DWMFRAMEINTERVAL` | `15` (with `--fps 60`) | Raises the remote-session frame cap from 30 fps to 60. Needs a reboot. |
| `bEnumerateHWBeforeSW` | `1` (with `--gpu`) | Lets the seat render on your real GPU instead of the software adapter. Needs a reboot. |

Setup starts or restarts Remote Desktop Services when needed and verifies the loopback listener.
A service restart may disconnect existing Remote Desktop sessions. `anode doctor` also checks the
listener: an enabled registry setting alone does not mean the machine is ready.

`anode setup --undo` turns child sessions back off, signing out a running seat first. It
deliberately leaves Remote Desktop enabled, because other software may depend on it; turn that off
yourself in Settings if you want it off. It also leaves the `--fps 60` and `--gpu` values;
[SECURITY.md](SECURITY.md#what-anode-setup-changes) shows how to remove them.

---

## Bring a seat up

```powershell
anode up                     # foreground, viewer visible
anode start                  # detached, returns when the seat is ready
anode start --hidden         # the same, with the viewer kept closed
anode up --width 1920 --height 1080 --audio
```

The viewer appears **without taking focus**, so a seat can come up mid-sentence without eating your
keystrokes. It starts in **view only**: your stray clicks do not reach the seat. Press
**Take control** when you want to drive it yourself.

Opening **Anode** from the Start menu (the installer adds it) or double-clicking `anode.exe` brings
the viewer to the front, starting Anode or a stopped seat first.

The status bar says **View only** or **You have control**. **Full screen** (F11) keeps the
**Stop seat**, **Release control** and **Exit full screen** buttons visible. **Details** expands
the complete status message, including troubleshooting steps and emergency-shortcut availability;
select text and press Ctrl+C to copy it. At narrow window sizes, secondary actions appear in the
toolbar's overflow menu. Closing or minimizing the viewer leaves the seat running.

Press **Sign in…** in the header next to **Reconnect** to open Windows' credential dialog.
This also retries a failed automatic sign-in without restarting Anode or signing out the seat.
The tray icon's menu offers the same **Sign in…** while the viewer is hidden.
Enter your Windows account password in that dialog; Anode does not retrieve or save it.

```powershell
anode status                 # what the seat is doing
anode show / anode hide      # the viewer, not the seat
anode kill                   # stop the seat
anode quit                   # stop the seat and exit Anode
```

---

## Launch a Steam game with a joystick

This is the walkthrough for the case Anode was built for.

```powershell
anode start | Out-Host                             # seat comes up, viewer visible
$env:ANODE_AGENT_ID = 'steam-demo'
$lease = anode lease acquire --ttl 600 | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
anode steam 220 | Out-Host                         # Half-Life 2, by its Steam app id
anode gamepad attach | Out-Host                    # a virtual Xbox 360 pad appears
anode gamepad stick left 0.8 0.0 | Out-Host
anode gamepad tap a | Out-Host
anode shot game.png | Out-Host                     # see what the seat sees
anode gamepad detach | Out-Host
anode lease release | Out-Host
```

The lease lasts ten minutes here. Run `anode lease renew --ttl 600 | Out-Host` during a longer
session; once it expires, input is refused until you acquire again.

**Read this before you try it.** Steam normally reuses one client per Windows user, and the seat runs as
the same user as your desktop. Whichever session Steam started in is the session its games open in.
So:

- **Steam not running anywhere:** `anode steam <appid>` starts Steam inside the seat first, and the
  game opens in the seat. This is the case you want.
- **Steam already running on your desktop:** the command refuses and tells you why, because the game
  can open on your screen or the existing client can be disrupted. Keep Steam there if you need it on
  your desktop. Moving it into the seat means giving up that desktop access.
  `anode steam <appid> --force` (MCP: `force: true`) overrides the check; it does not create two
  independent clients.

`anode steam status` says which case you are in, in one line. The generic `anode run` command also
refuses direct Steam clients and `steam://` URLs while Steam is running outside the seat. Starting
another client can disrupt the existing client; it does not reliably provide two independent ones.

`anode run "D:\Games\thing\game.exe"` starts the process inside the seat. Some applications hand
work to an existing instance in another session, so verify the resulting application's session.
This direct Steam check does not inspect the contents of shortcuts or wrapper scripts.

For a game, `anode setup --fps 60 --gpu` and a borderless-window (not exclusive fullscreen) display
mode make the difference between unplayable and fine.

---

## Drive the seat

Anode also exposes window discovery, accessible controls, text and direct control
actions. These run inside the seat without a foreground computer-use overlay:

```powershell
anode windows --query notepad
anode inspect <windowId> --html inspection.html
anode element <snapshotId> <elementId> invoke
```

Inspection produces a readable tree or structured JSON, and can save a searchable
HTML report with a screenshot. Use the returned identifiers and offered actions.
See [Desktop tools](DESKTOP-TOOLS.md) for the complete CLI/MCP workflow and limits.

```powershell
anode shot [file] [--width 1000] [--jpeg]
anode click 640 400 [--right] [--double]
anode move 640 400
anode scroll -3
anode key ctrl+shift+esc
anode type "hello from the seat"
anode ps                     # windowed programs in the seat; --all for every process
anode ps kill notepad
```

Coordinates are pixels on the seat's original screen. If you scale a screenshot down, convert image
coordinates back to that screen size: `x * sourceWidth / width` and `y * sourceHeight / height`.

Keys go in as **scan codes**, so games that read raw input or DirectInput actually see them.

### Virtual gamepad

```powershell
anode gamepad attach [--slot 0]
anode gamepad tap a [--ms 80]
anode gamepad hold rb
anode gamepad release rb
anode gamepad stick left 0.5 -0.25    # x and y from -1 to 1
anode gamepad stick right 0 0
anode gamepad reset
anode gamepad detach
```

One honest caveat: a ViGEm pad is a real HID device plugged into the **machine**, not into a session.
Every session sees it, exactly like a controller in a USB port. That is what makes it work for a game
in the seat, and it also means a game on your own screen can read it. Anode unplugs it when the seat
stops.

---

## Command reference

`anode help` lists every command, and `anode help <command>` or `anode <command> --help` shows one
without running it. Releases up to 0.5.0 ignore `--help` after a command and run it (`anode kill
--help` stops the seat), so use `anode help <command>` there. In PowerShell, append `| Out-Host` so the prompt waits for output and sets
`$LASTEXITCODE`. Commands marked *lease* need a [desktop lease](MULTI-AGENT.md#cli-workflow); give
the agent ID and token through `ANODE_AGENT_ID` and `ANODE_LEASE_TOKEN`, or as
`--agent ID --lease TOKEN` **before** the command. Usage errors exit with code 2 before anything
runs: an unknown command, an unknown option on commands such as `setup`, `start` and `kill`, or
malformed input such as a single click coordinate.

### Start here

| Command | What it does |
| --- | --- |
| `anode doctor [--json]` | Check whether this machine can host a seat; changes nothing. Exits 1 when a check fails. `--json` prints `ready`, `version` and `checks`. |
| `anode setup [--fps 60] [--gpu]` | Enable Remote Desktop and child sessions (one UAC prompt). `--fps 60` and `--gpu` add the two game settings. |
| `anode setup --undo` | Turn child sessions back off, signing out a running seat. |
| `anode configure [auto\|codex\|claude\|both] [--no-skill]` | Register installed Codex/Claude Code CLIs and the `anode-desktop` skill, with backups. Restart the agent afterward. |
| `anode start [options]` | Start Anode detached; returns when the seat is ready. `--hidden` keeps the viewer closed. |
| `anode capabilities [--json] [--no-capture]` | Report seat capture and known input blockers. `--no-capture` skips the capture probe. |

### Run the seat

| Command | What it does |
| --- | --- |
| `anode up [options]` | Run Anode in this process; the viewer opens unless `--hidden`, and startup errors print here. |
| `anode status [--json]` | What the seat is doing. With nothing running it exits 1, and `--json` then prints `state` `"stopped"`, `daemonRunning` `false`, `agentId`, `ownerAgentId` `null` and `summary`. |
| `anode show`, `anode hide` | Show or hide the viewer window. The seat keeps running. |
| `anode control [take\|give]` | `take` lets your own mouse and keyboard reach the seat; `give`, the default, returns to view only. |
| `anode rendering [--restore]` | Check the per-user RDP rendering preference, or restore the value saved before Anode changed it. |
| `anode kill` (alias `stop`) | Sign the seat out and close every program in it, for every agent (ignores leases). Anode keeps running. |
| `anode quit` | Stop the seat and exit Anode; returns once Anode has exited. |

### Agents and leases

Every `lease` action needs an agent ID; `renew` and `release` also need the token.

| Command | What it does |
| --- | --- |
| `anode lease acquire [--ttl SECONDS]` | Acquire exclusive desktop use for 10-600 seconds (default 120). Starts a hidden seat if needed. Prints the lease as JSON, including `leaseToken`. |
| `anode lease renew [--ttl SECONDS]` | Extend your live lease. |
| `anode lease release [--cancel-jobs]` | Give up the desktop. `--cancel-jobs` also cancels your running command jobs. |
| `anode lease [status]` | The owner and time remaining. Never starts anything. |
| `anode guide [--json]` | The agent guide: when to use Anode and how to choose its tools. Needs no setup. |
| `anode mcp` | The MCP server on stdin/stdout, which agent clients launch. |

### Work in the seat

| Command | What it does |
| --- | --- |
| `anode run <program> [args...]` | *Lease.* Start a program, document or shortcut inside the seat. |
| `anode steam [status]` | Say where Steam is running. |
| `anode steam <appid> [--force]` | *Lease.* Start a Steam game in the seat. `--force` launches even while Steam runs outside the seat. |
| `anode ps [--all]` | Windowed programs in the seat; `--all` lists every process. |
| `anode ps kill <pid\|name>` | *Lease.* Close one program in the seat. |
| `anode shot [file] [--width N] [--jpeg]` (alias `screenshot`) | *Lease.* Save a seat screenshot, by default `anode-<time>.png` in the current folder. |
| `anode windows [--query text] [--pid N] [--json]` | *Lease.* List seat windows and their IDs. |
| `anode inspect <windowId> [--html file] [--image file] [--json]` | *Lease.* Read a window's controls. Limits: `--max-elements N` (1-500), `--max-depth N` (0-20), `--max-text N` (0-20000), `--offscreen`. |
| `anode window <windowId> <action>` | *Lease.* `focus`, `raise`, `restore`, `maximize`, `minimize`, `close`, or `move` with `--x N --y N --width N --height N`. |
| `anode element <snapshotId> <elementId> <action>` | *Lease.* `invoke`, `focus`, `set_value --value text`, `toggle`, `select`, `expand`, `collapse`, `scroll_into_view`, `scroll --direction up\|down\|left\|right [--amount small\|large]` or `set_range --number N`. |
| `anode wait <windowId> [selectors] [--state S] [--wait MS] [--json]` | *Lease.* Wait for a control. Selectors `--automation-id`, `--name`, `--role` and `--text` combine; give at least one. `--state` is `exists` (default), `missing`, `enabled` or `disabled`; `missing` cannot use `--text`. Waits up to 30000 ms (default 10000) and exits 3 when unmatched. |

### Drive the seat

| Command | What it does |
| --- | --- |
| `anode click [x y] [--right\|--middle] [--double]` | *Lease.* Click at a point, or where the seat's pointer is when no coordinates are given. |
| `anode move <x> <y>` | *Lease.* Move the seat's pointer. |
| `anode scroll [notches]` | *Lease.* Scroll the wheel; negative is down, default -3. |
| `anode key <chord>` | *Lease.* Press one key or chord, such as `ctrl+shift+esc`. |
| `anode type <text>` | *Lease.* Type literal text into the focused control. |

### Virtual controller

These need the [ViGEm bus driver](TROUBLESHOOTING.md#the-vigem-bus-driver-did-not-respond).
`pad` is an alias of `gamepad`, and every subcommand accepts `--slot N` (0-3, default 0).

| Command | What it does |
| --- | --- |
| `anode gamepad attach`, `detach`, `reset` | *Lease.* Plug in, unplug, or release every button and center both sticks. |
| `anode gamepad [state]` | List the attached virtual controller slots. |
| `anode gamepad tap <button> [--ms 80]` | *Lease.* Press a button or trigger briefly. |
| `anode gamepad hold <button>`, `release <button>` | *Lease.* Hold or release a button. |
| `anode gamepad stick <left\|right> <x> <y>` | *Lease.* Set a stick, each axis from -1 to 1. |

### Develop and test

| Command | What it does |
| --- | --- |
| `anode exec [--cwd DIR] [--timeout MS] [--wait MS] [--env NAME=VALUE]... [--json] -- <program> [args...]` | *Lease.* Start a command job. `--timeout` is its hard lifetime, 100-1800000 ms (default 120000); `--wait` is the initial wait, 0-10000 ms (default 1000). `--env` repeats. |
| `anode job <jobId> [--after cursor] [--wait MS] [--max-chars N] [--cancel] [--json]` | Read a job's output, or cancel it. Needs your agent ID but no lease. |
| `anode jobs [--json]` | List your job IDs after a disconnected client. Needs your agent ID. |

### Diagnose

| Command | What it does |
| --- | --- |
| `anode selftest --quick` (alias `self-test`) | Anode's own checks on private pipes; no seat, capture or input. |
| `anode selftest [--no-gamepad] [--no-scheduler]` | Also captures your current desktop, runs a Task Scheduler check and attaches a machine-wide virtual gamepad. `--no-scheduler` skips the capture and Task Scheduler checks; `--no-gamepad` skips the gamepad. |
| `anode version` | Print `anode x.y.z`. |
| `anode help [command]` | Print all commands, or one. |

### Options for `up` and `start`

`start` passes these to a new Anode only; with Anode already running it just brings the seat up.

| Option | Effect |
| --- | --- |
| `--width N --height N` | Seat resolution; default 1280x720. |
| `--scale N` | DPI scale percentage for the seat, above 100 and up to 500. |
| `--no-scaling` | Show the seat at its real size instead of fitting it to the viewer window. |
| `--audio` | Play the seat's sound on this computer (off by default). |
| `--clipboard` | Share your clipboard with the seat (off by default). |
| `--winkeys` | Send Windows-key shortcuts to the seat, not to you. |
| `--control` | Start with your input reaching the seat instead of view only. |
| `--hidden` | Keep the viewer window closed. |
| `--sign-in` | Ask Windows for seat credentials without signing you out. Needs a visible viewer, and a running Anode refuses it. |
| `--no-background-rendering` | Leave the per-user RDP rendering preference unchanged. |
| `--state-dir PATH` | Absolute directory for logs and state when starting a new Anode. |
| `--keep` | Leave the seat running when Anode exits. |

### Exit codes

`0` success, `1` failure, `2` usage error or Anode already running, `3` `start`, `up` or
`lease acquire` blocked by missing prerequisites, or a `wait` that did not match, `1223` setup prompt declined. `exec` and `job` return the program's own exit
code when it completed, so `1` or `2` may come from the program (read stderr); `124` means it timed
out, `130` that it was cancelled, and `0` with a job ID that it is still running.

### MCP tool names

Most MCP tools match a CLI command: `seat_windows` is `windows` and `gamepad_tap` is
`gamepad tap`. The others: `seat_observe` is `inspect`, `seat_screenshot` is `shot`,
`seat_processes` is `ps`, `seat_kill_process` is `ps kill`, `seat_stop` is `kill`, `steam_status`
and `steam_launch` are `steam`, `seat_job` is `job` and `jobs`, `gamepad_set` is `gamepad hold`,
`release` and `stick`, and `anode_guide` is `guide`. `seat_drag` has no CLI command.

---

## For agents: the MCP server

Run `anode configure` to register installed Codex and Claude Code CLIs.
See [Connecting agents](CONNECTING-AGENTS.md) and the [protocol reference](PROTOCOL.md) for configuration and all 32 tools.

## How it works

```
your session (session 1)                       the seat (child session, session N)
+---------------------------------------+      +------------------------------------+
|  anode up  (daemon)                   |      |  anode __seat-host                 |
|   - Remote Desktop ActiveX control    |      |   - SendInput  (this session only) |
|     with ConnectToChildSession = true |      |   - screen capture                 |
|   - viewer window + Stop seat         |      |   - starts programs and Steam      |
|   - control pipe                      |      |   - ViGEm virtual gamepad          |
|     \\.\pipe\anode-control            |      |                                    |
+---------------|-----------------------+      +------------------|-----------------+
                |                                                 |
        anode CLI, anode mcp  ------------------------------------+
                                   \\.\pipe\anode-seat
```

1. The daemon hosts the Remote Desktop ActiveX control, sets `ConnectToChildSession`, and connects to
   `localhost`. Windows signs a child session in with your existing credentials when delegation is
   available. `--sign-in` requests the native Windows credential dialog when needed.
2. It waits for completed Windows login and `WTSGetChildSessionId`, then uses the Task Scheduler's `RunEx` with
   `TASK_RUN_USE_SESSION_ID` to place one process, the seat host, inside that session. This is the
   only way a process in one session can start a process in another, and it needs no elevation.
3. From then on the seat host is an ordinary interactive process in the seat. **This is where the
   isolation comes from**: `SendInput` posts to its own session's input queue, screen capture reads
   its own desktop, and programs it starts are its children. Input and capture execute inside the verified child session; files, account state and gamepads remain shared.
   The seat host refuses to run at all if it finds itself outside the child session.
4. Stopping means `WTSLogoffSession` on the child session, which force-terminates everything in it.
   If that ever fails, Anode falls back to killing the session's processes directly.

More in [docs/ARCHITECTURE.md](ARCHITECTURE.md) and [docs/PROTOCOL.md](PROTOCOL.md).

---

## Limits, honestly

- **One seat at a time.** Windows allows exactly one connected child session per machine.
- **Steam is one instance per user.** See the walkthrough above.
- **Apps that refuse to run twice** (browsers with the same profile, Office) will not run in both
  sessions at once. Browsers work if you give the seat its own profile directory.
- **Startup programs run in the seat too.** Anything in your Run key or Startup folder launches when
  the seat signs in, which can mean two copies of a tray app.
- **PIN sign-in.** If automatic seat sign-in fails after PIN/Windows Hello login, press **Sign in…**
  in the viewer header or the tray menu (or start Anode with `anode start --sign-in`). Windows asks
  for credentials while your current desktop stays signed in. Anode does not retrieve or save the
  password; a new seat may require the prompt again.
- **Exclusive fullscreen and protected video** do not capture. Use borderless windowed.
- **Performance is a remote-session pipeline**, not a monitor cable. Without `--gpu` the seat renders
  on a software adapter. With it, and with `--fps 60`, ordinary games are fine; competitive twitch
  play is not what this is.
- **The machine cannot restart or shut down** while a child session is connected. Stop the seat first.
- **`anode setup` enables Remote Desktop**, a real security-relevant setting, even though no firewall
  port is opened. Read [docs/SECURITY.md](SECURITY.md) and decide for yourself.

Something wrong? [docs/TROUBLESHOOTING.md](TROUBLESHOOTING.md), and the log at
`%LOCALAPPDATA%\Anode\anode.log`.

---

## Build and test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest   # publish and verify dist\anode.exe
dotnet build Anode.sln
anode selftest          # also captures the desktop and tests a machine-wide gamepad
anode selftest --quick  # includes MCP discovery, pipe deadlines and identity checks (what CI runs)
```

If a running Anode or MCP client uses `dist\anode.exe`, publish elsewhere with
`-OutputDirectory artifacts\pkg-build`; see [Contributing](../CONTRIBUTING.md#build-while-anode-is-running).
CI builds on `windows-latest` and runs the quick self-test on every push and pull request.
