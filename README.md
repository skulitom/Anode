# Anode

**A virtual seat for agents on Windows.** A second, fully isolated Windows session on your own
machine, with its own screen, its own mouse pointer, its own keyboard focus and its own running
programs. An agent works in the seat while you keep working on your desktop, and neither one
steals the other's clicks.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Windows build](https://github.com/skulitom/Anode/actions/workflows/build.yml/badge.svg)](https://github.com/skulitom/Anode/actions/workflows/build.yml)

```powershell
anode setup            # one administrator prompt, once per machine
anode start            # a seat comes up; a small window lets you watch it
anode steam 570        # Dota 2 launches inside the seat, not on your screen
anode gamepad attach   # a virtual Xbox controller the agent can drive
anode kill             # sign the seat out; everything in it closes at once
```

---

## Why a second session and not a second monitor

The obvious way to give an agent somewhere to work is a virtual display, and it is the wrong way.
A virtual monitor is still your session. There is one mouse pointer, one keyboard focus, one
foreground window and one clipboard, shared between you and the agent. The moment the agent
clicks something, your typing goes somewhere you did not expect.

Windows already has the right primitive: a **child session**. It is a loopback Remote Desktop
session tied to your signed-in session, documented since Windows 8, and it is a genuine second
seat at the machine:

|                                   | Virtual monitor | Child session (Anode)   |
| --------------------------------- | --------------- | ----------------------- |
| Separate mouse pointer            | no              | **yes**                 |
| Separate keyboard focus           | no              | **yes**                 |
| Separate foreground window        | no              | **yes**                 |
| Separate clipboard                | no              | **yes** (opt in to share) |
| Sees your files and installed apps | yes            | **yes**                 |
| Stop everything at once           | no              | **yes**, one sign-out   |

The seat signs in as you, so it reaches the same drives, the same programs and the same Steam
library. It has no physical display, it never appears on your monitors, and it dies with your
session, so nothing is left running after you sign out.

If you want the opposite thing, a beautiful CRT view of a *virtual monitor* in your own session,
that is [Cathode](https://github.com/skulitom/CathodeDisplay). Anode is its counterpart: same
machine, other electrode, real isolation.

---

## What you get

- **A seat.** A second Windows desktop at a resolution you choose, running as you.
- **A viewer.** A small window showing the seat live, with a red **Stop seat** button. Close it and
  the seat keeps running; it lives in the tray.
- **A kill switch that always works.** `Ctrl+Alt+Shift+K` from anywhere, the tray icon, the toolbar
  button, or `anode kill`. All four sign the child session out, which force-closes every program in
  it, however wedged.
- **A virtual gamepad.** An Xbox 360 controller, via the ViGEm bus driver, that an agent can hold,
  tap and steer.
- **A CLI** for everything, and an **MCP server** so an agent can hold the seat as a tool.

---

## Requirements

| | |
| --- | --- |
| Windows | 10 or 11, **Pro, Enterprise, Education**, or Server. Home has no Remote Desktop host, so it cannot host a child session. |
| Build | .NET 8 SDK to build from source. Release builds are self-contained and need no runtime installed. |
| Gamepad | [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) for the virtual controller. Everything else works without it. |

Check your machine before changing anything:

```powershell
anode doctor      # what the machine still needs
anode selftest    # exercise the parts that do not need a seat
```

---

## Install

```powershell
git clone https://github.com/skulitom/Anode.git
cd Anode
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

That produces a single self-contained `dist\anode.exe`. Put it on your `PATH`, or call it by path.

---

## Setup, once per machine

```powershell
anode setup                 # the two settings a seat needs
anode setup --fps 60 --gpu  # and the two that make games usable
```

You will see one UAC prompt. Decline it and nothing changes. Exactly what it does, and nothing
else, is listed in [docs/SECURITY.md](docs/SECURITY.md):

| Setting | Value | Why |
| --- | --- | --- |
| `fDenyTSConnections` | `0` | The machine needs a Remote Desktop host for a loopback session to connect to. **No firewall rule is added**; network access depends on your existing firewall rules. |
| `WTSEnableChildSessions` | `TRUE` | The documented switch for the feature. |
| `DWMFRAMEINTERVAL` | `15` (with `--fps 60`) | Raises the remote-session frame cap from 30 fps to 60. Needs a reboot. |
| `bEnumerateHWBeforeSW` | `1` (with `--gpu`) | Lets the seat render on your real GPU instead of the software adapter. Needs a reboot. |

Setup starts or restarts Remote Desktop Services when needed and verifies the loopback listener.
A service restart may disconnect existing Remote Desktop sessions. `anode doctor` also checks the
listener: an enabled registry setting alone does not mean the machine is ready.

`anode setup --undo` turns child sessions back off. It deliberately leaves Remote Desktop enabled,
because other software may depend on it; turn that off yourself in Settings if you want it off.

---

## Bring a seat up

```powershell
anode up                     # foreground, viewer visible
anode start                  # detached, returns when the seat is ready
anode up --width 1920 --height 1080 --audio
```

The viewer appears **without taking focus**, so a seat can come up mid-sentence without eating your
keystrokes. It starts in **view only**: your stray clicks do not reach the seat. Press
**Take control** when you want to drive it yourself.

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
anode start                  # seat comes up
anode steam 220              # Half-Life 2, by its Steam app id
anode gamepad attach         # a virtual Xbox 360 pad appears
anode gamepad stick left 0.8 0.0
anode gamepad tap a
anode shot game.png          # see what the seat sees
```

**Read this before you try it.** Steam normally reuses one client per Windows user, and the seat runs as
the same user as your desktop. Whichever session Steam started in is the session its games open in.
So:

- **Steam not running anywhere:** `anode steam <appid>` starts Steam inside the seat first, and the
  game opens in the seat. This is the case you want.
- **Steam already running on your desktop:** the command refuses and tells you why, because the game
  can open on your screen or the existing client can be disrupted. Keep Steam there if you need it on
  your desktop. Moving it into the seat means giving up that desktop access. `force: true` overrides
  the check; it does not create two independent clients.

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
See [Desktop tools](docs/DESKTOP-TOOLS.md) for the complete CLI/MCP workflow and limits.

```powershell
anode shot [file] [--width 1000] [--jpeg]
anode click 640 400 [--right] [--double]
anode move 640 400
anode scroll -3
anode key ctrl+shift+esc
anode type "hello from the seat"
anode ps                     # what is running in the seat
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

## For agents: the MCP server

`anode mcp` speaks the Model Context Protocol on stdin and stdout. It owns nothing; every call goes
to the running daemon, so you and the agent point at the same seat and your stop button still wins.
It starts the daemon on the first call if it is not up, detached through the Task Scheduler so the
seat outlives the agent that asked for it.

**Claude Code**

```powershell
claude mcp add -s user anode -- "$env:USERPROFILE\.local\bin\anode.exe" mcp
```

**Codex CLI**

```powershell
codex mcp add anode -- "$env:USERPROFILE\.local\bin\anode.exe" mcp
```

**Claude Desktop** (`claude_desktop_config.json`), or any MCP client:

```json
{
  "mcpServers": {
    "anode": {
      "command": "C:\\Users\\you\\.local\\bin\\anode.exe",
      "args": ["mcp"]
    }
  }
}
```

Twenty-two tools, grouped:

| | |
| --- | --- |
| Seat | `seat_status` `seat_start` `seat_stop` `seat_show` `seat_hide` |
| Look | `seat_screenshot` `seat_processes` |
| Programs | `seat_run` `seat_kill_process` `steam_status` `steam_launch` |
| Input | `seat_click` `seat_move` `seat_drag` `seat_scroll` `seat_key` `seat_type` |
| Gamepad | `gamepad_attach` `gamepad_detach` `gamepad_set` `gamepad_tap` `gamepad_reset` |

`seat_screenshot` returns a real image block, so a model can look at the seat.

Full setup for both terminal agents, what to put in `CLAUDE.md` or `AGENTS.md`, and the alternative
of running the agent *inside* the seat: [docs/CONNECTING-AGENTS.md](docs/CONNECTING-AGENTS.md).
A worked session: [examples/claude-code](examples/claude-code).

---

## How it works

```
your session (session 1)                      the seat (child session, session N)
+--------------------------------------+      +------------------------------------+
|  anode up  (daemon)                   |      |  anode __seat-host                 |
|   - Remote Desktop ActiveX control    |      |   - SendInput  (this session only) |
|     with ConnectToChildSession = true |      |   - screen capture                 |
|   - viewer window + Stop seat         |      |   - starts programs and Steam      |
|   - control pipe  \\.\pipe\anode-control     |   - ViGEm virtual gamepad          |
+---------------|----------------------+      +------------------|-----------------+
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
   its own desktop, and programs it starts are its children. Nothing it does can reach your session.
   The seat host refuses to run at all if it finds itself outside the child session.
4. Stopping means `WTSLogoffSession` on the child session, which force-terminates everything in it.
   If that ever fails, Anode falls back to killing the session's processes directly.

More in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/PROTOCOL.md](docs/PROTOCOL.md).

---

## Limits, honestly

- **One seat at a time.** Windows allows exactly one connected child session per machine.
- **Steam is one instance per user.** See the walkthrough above.
- **Apps that refuse to run twice** (browsers with the same profile, Office) will not run in both
  sessions at once. Browsers work if you give the seat its own profile directory.
- **Startup programs run in the seat too.** Anything in your Run key or Startup folder launches when
  the seat signs in, which can mean two copies of a tray app.
- **PIN sign-in.** If automatic seat sign-in fails after PIN/Windows Hello login, start a new daemon
  with `anode start --sign-in`. Windows asks for the seat credentials in its own dialog while your
  current desktop stays signed in. Anode does not retrieve or save the password; a new seat may
  require the prompt again.
- **Exclusive fullscreen and protected video** do not capture. Use borderless windowed.
- **Performance is a remote-session pipeline**, not a monitor cable. Without `--gpu` the seat renders
  on a software adapter. With it, and with `--fps 60`, ordinary games are fine; competitive twitch
  play is not what this is.
- **The machine cannot restart or shut down** while a child session is connected. Stop the seat first.
- **`anode setup` enables Remote Desktop**, a real security-relevant setting, even though no firewall
  port is opened. Read [docs/SECURITY.md](docs/SECURITY.md) and decide for yourself.

Something wrong? [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md), and the log at
`%LOCALAPPDATA%\Anode\anode.log`.

---

## Build and test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest   # publish and verify dist\anode.exe
dotnet build Anode.sln
anode selftest          # everything that does not need a seat
anode selftest --quick  # includes MCP discovery, pipe deadlines and identity checks (what CI runs)
```

CI builds on `windows-latest` and runs the quick self-test on every push and pull request.

---

## Credits

Built on documented Windows APIs: [child sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions),
the [Remote Desktop ActiveX control](https://learn.microsoft.com/en-us/windows/win32/termserv/remote-desktop-activex-control),
and [`IRegisteredTask::RunEx`](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex).
The virtual controller uses [ViGEm](https://github.com/nefarius/ViGEmBus) by Nefarius.
Prior art worth reading if you are going down this road: UiPath and Power Automate's picture-in-picture
modes, [LibreAutomate](https://github.com/qgindi/LibreAutomate)'s PiP, and
[BetterGI](https://github.com/babalae/better-genshin-impact)'s child-session work.

## License

[MIT](LICENSE).
