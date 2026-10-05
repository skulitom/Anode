# <img src="assets/anode.svg" width="36" height="36" alt=""> Anode — a background Windows desktop for AI agents

<!-- mcp-name: io.github.skulitom/anode -->

**Computer-use agents that drive your Windows desktop take over your mouse and keyboard; Anode gives them their own desktop instead.**
Anode is a Windows MCP server and CLI for computer use, GUI automation and UI testing. Claude Code,
Codex or another local (stdio) MCP client gets a hidden Windows session with its own screen, pointer and keyboard focus,
called the *seat*. The agent drives native apps and headed browsers and takes screenshots while you
keep working in yours.

<!-- DEMO GIF: record assets/demo.gif (under 8 MB), then replace this comment with: ![An agent testing an app in Anode's background seat while the user keeps working](assets/demo.gif) -->

[![Release](https://img.shields.io/github/v/release/skulitom/Anode)](https://github.com/skulitom/Anode/releases/latest)
[![Windows build](https://github.com/skulitom/Anode/actions/workflows/build.yml/badge.svg)](https://github.com/skulitom/Anode/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Install in 3 commands

**Before you start:** Anode needs Windows 10/11 Pro, Enterprise or Education (not Home) on x64, and
administrator rights once, for `anode setup`. On a managed work PC, a policy that blocks unsigned
programs or keeps Remote Desktop off will stop it; [see exactly what setup changes](docs/SECURITY.md#what-anode-setup-changes).

In PowerShell, run the first line, then open a **new terminal** for the next two:

```powershell
iwr -UseBasicParsing https://github.com/skulitom/Anode/releases/latest/download/install.ps1 -OutFile install.ps1; powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
anode setup | Out-Host       # in a NEW terminal: one administrator prompt, once per machine
anode configure | Out-Host   # registers Anode with the Claude Code and/or Codex CLIs on your PATH
```

`anode setup` can restart Remote Desktop Services, which disconnects any Remote Desktop session to
this PC, so run it at the machine itself. If you sign in to Windows only with a PIN or Windows Hello,
the seat needs your Windows password to sign in, and Windows may ask for it again when you create
another seat: see [sign-in](docs/TROUBLESHOOTING.md#it-asks-for-a-password-every-time).

Restart your agent session and ask it:

> Use Anode to open Notepad in the background seat, type "Hello from Anode", and show me a screenshot.
> Keep the viewer hidden and leave my main desktop available.

See [Install](#install) for custom locations and offline installs, or
[other ways to install](#other-ways-to-install) for Scoop and the Claude Code plugin.

**Good to know:** the seat is a Windows child session running as your user: desktop isolation, not a security sandbox.
**Ctrl+Alt+Shift+K** stops it (if another program holds that shortcut, use **Stop seat** in the viewer or
`anode kill`). Unsigned builds may trigger SmartScreen. No telemetry.

## What can you do with it?

- **Develop and test apps:** run builds and local servers, collect command output, inspect native
  controls, test browser forms and capture screenshots in the background desktop.
- **Give an agent a desktop:** 41 MCP tools for guidance, screenshots, audio, mouse/keyboard input, accessibility,
  UI waits, display changes, Android emulators, application launches and cancellable command jobs.
- **Watch or stop it:** `anode show` opens the viewer and `anode hide` closes it. **Ctrl+Alt+Shift+K**
  or `anode kill` signs the seat out and closes every application in it, including unsaved work.
- **Automate games:** optional virtual Xbox 360 controller support through ViGEmBus, kept inside the
  seat with HidHide so you can play your own games meanwhile.
- **Coordinate several agents:** exclusive desktop leases and command jobs scoped to each agent.
  Agents share one seat and take turns; see [multiple agents](docs/MULTI-AGENT.md).
- **Listen and play sound:** with `--audio`, set when Anode starts, agents can record what plays in the
  seat, such as a browser or a game, and play short 16-bit PCM WAV clips. This also sends seat sound to your speakers. See [audio](docs/AUDIO.md).
- **Test other displays:** agents change the seat's resolution and Windows scaling while its apps
  keep running, from 640x480 to 8192x8192, portrait included, at Windows' scaling steps from 100% to
  500%; Anode restores the startup display when they finish and reports what Windows actually applied. See [test other displays](docs/DISPLAYS.md).
- **Test Android apps and use web consoles:** agents boot your AVDs read-only on an emulator in the
  seat, drive them through adb, open Android Studio there on its own profile, and open web consoles
  such as Google Play Console in a seat browser profile you sign in to (sites may ask you to sign in again). See
  [Android apps and web consoles](docs/ANDROID.md).

The seat is a Windows *child session*: a second session for your own account, created by Windows'
built-in loopback Remote Desktop feature. It has its own pointer and focus; adding a virtual
monitor to your current session does not. The seat runs as your Windows user and shares your
files, account and network. **This is desktop isolation, not a security sandbox.** Virtual
gamepads stay in the seat only with HidHide installed. See [security and isolation](docs/SECURITY.md).

## Install

**Requires:** Windows 10/11 **Pro, Enterprise or Education**, or Windows Server with a Remote
Desktop host. The release targets **x64**; ARM64 is not validated. **Windows Home is unsupported.**
Release builds include .NET; you do not need an SDK or runtime. ViGEmBus is optional.

Run in an ordinary PowerShell terminal:

```powershell
Invoke-WebRequest -UseBasicParsing https://github.com/skulitom/Anode/releases/latest/download/install.ps1 -OutFile install.ps1
# You can inspect install.ps1 before running it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

The installer verifies the release archive's SHA-256 checksum, installs to
`%LOCALAPPDATA%\Programs\Anode`, adds that folder to your user PATH and adds **Anode** to the
Start menu, where opening it shows the viewer, and to Installed apps, where you can uninstall it.
**Open a new terminal** afterward. Machine setup and agent registration are separate steps below.

Prefer portable? [Download the ZIP](https://github.com/skulitom/Anode/releases/latest/download/anode-windows-x64.zip),
extract the entire folder, and use `.\anode.exe` in place of `anode`.
Builds are currently unsigned; Windows may show a SmartScreen warning.
[Custom locations, offline installation, updates and removal →](docs/INSTALL.md)

### Other ways to install

Both still need the one-time `anode setup` below; plugin users skip `anode configure` for
Claude Code.

- **Scoop:** `scoop bucket add anode https://github.com/skulitom/Anode`, then
  `scoop install anode/anode`. Run `anode quit` and close MCP clients using Anode before
  `scoop update anode`.
- **Claude Code plugin:** with `anode` already on your PATH (the installer above or Scoop), run
  `/plugin marketplace add skulitom/Anode`, then `/plugin install anode@anode` inside Claude Code.
  It registers the MCP server and the `anode-desktop` skill but does not install `anode.exe`.
  Use it or `anode configure claude`, not both; see [Claude Code](docs/CONNECTING-AGENTS.md#1-claude-code).

## Set up and connect

```powershell
anode doctor | Out-Host           # reports prerequisites; no changes
anode setup | Out-Host            # one administrator prompt, once per machine
anode configure | Out-Host        # detects installed Codex / Claude Code CLIs
```

`setup` enables Remote Desktop and child sessions. It adds no firewall rules, but can restart
Remote Desktop Services and disconnect existing RDP sessions.
[See exactly what changes](docs/SECURITY.md#what-anode-setup-changes).

`configure` backs up client settings, registers Anode by absolute path, sets a 420-second tool
timeout, and installs the discoverable `anode-desktop` skill. Use `anode configure codex` or
`anode configure claude` to choose one client, or add `--no-skill` for MCP registration alone.
Installed the Claude Code plugin? It already registers Anode and the skill there, so skip
`anode configure`, or run `anode configure codex` to add Codex only.
**Restart your agent session** to load the tools. Claude Desktop:
[add Anode to its config](docs/CONNECTING-AGENTS.md#claude-desktop).
VS Code, Cursor and other clients: [Connecting agents](docs/CONNECTING-AGENTS.md#3-any-other-mcp-client).

## Try it

Start and check the background seat:

```powershell
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

If Windows Hello/PIN sign-in prevents automatic login, choose **Sign in…** from the Anode tray
icon (or the viewer's header) to request the Windows credential dialog. When starting Anode,
you can also use `anode start --sign-in`. See [sign-in troubleshooting](docs/TROUBLESHOOTING.md#it-asks-for-a-password-every-time).

Then ask your agent:

> Use Anode to open Notepad in the background seat, type "Hello from Anode", and show me a
> screenshot. Keep the viewer hidden and leave my main desktop available.

Or work through the CLI:

```powershell
$env:ANODE_AGENT_ID = 'my-cli-task'
$lease = anode lease acquire | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
anode exec --cwd C:\project --wait 1000 -- dotnet build | Out-Host
anode windows | Out-Host
anode shot seat.png | Out-Host
anode lease release | Out-Host
```

PowerShell users: piping to `Out-Host` (or `Out-String`) makes PowerShell wait for this GUI
executable and display its output. Scripts should check `$LASTEXITCODE` after each command.

## For agents

Prefer Anode for native Windows GUI automation and headed app/browser tests that should leave
the user's main desktop available; use direct APIs, file tools and headless tests otherwise.
Call `anode_guide` or run `anode guide --json` for setup-free guidance. `seat_status` never starts
anything. Call `seat_lease` with `action: "acquire"` before desktop work; it starts a hidden seat
if needed and waits in line while another agent works. Each desktop action keeps the lease, so
renew only across pauses longer than 120 seconds, and release when finished. The MCP prompt
`desktop_test` walks through it: `/mcp__anode__desktop_test` in Claude Code after
`anode configure`, or type `/` and search for `desktop_test` with the plugin.
See [the agent guide](docs/FOR-AGENTS.md), [portable skill](skills/anode-desktop/SKILL.md)
and [llms.txt documentation index](llms.txt).

## How it works

```mermaid
flowchart LR
  subgraph yours["Your session: your mouse, keyboard and apps"]
    agent["Claude Code / Codex / any MCP client"] -- "MCP (stdio)" --> mcp["anode mcp"]
    mcp -- "named pipe" --> daemon["Anode daemon<br/>viewer + Stop button"]
  end
  subgraph seat["The seat: a background Windows child session"]
    host["seat host<br/>screenshots, input, UI Automation, command jobs"] --> apps["apps, browsers, builds, games"]
  end
  daemon -- "named pipe" --> host
```

## Documentation

| I want to… | Start here |
| --- | --- |
| Install, update or uninstall | [Installation guide](docs/INSTALL.md) |
| Connect Codex, Claude Code, Claude Desktop, VS Code or Cursor | [Connecting agents](docs/CONNECTING-AGENTS.md) |
| Share the desktop between agents | [Multiple agents and leases](docs/MULTI-AGENT.md) |
| Help an agent discover and choose Anode | [Agent guide](docs/FOR-AGENTS.md) |
| Run apps, control the viewer or use a gamepad | [Usage guide](docs/USAGE.md) |
| Look up a CLI command or option | [Command reference](docs/USAGE.md#command-reference) |
| Test native apps and browsers | [Development and testing](docs/DEVELOPMENT-TESTING.md) |
| Test apps at other resolutions and scaling | [Test other displays](docs/DISPLAYS.md) |
| Test Android apps, or work in web consoles as you | [Android apps and web consoles](docs/ANDROID.md) |
| See worked examples | [Claude Code + Steam](https://github.com/skulitom/Anode/blob/main/examples/claude-code/README.md), [headed Playwright fixture used by the development test](https://github.com/skulitom/Anode/blob/main/examples/development/browser-check.cjs) |
| Inspect windows and accessible controls | [Desktop tools](docs/DESKTOP-TOOLS.md) |
| Understand permissions and isolation | [Security](docs/SECURITY.md) |
| Know what data Anode handles | [Privacy](docs/PRIVACY.md) |
| Diagnose startup, sign-in or capture problems | [Troubleshooting](docs/TROUBLESHOOTING.md) |
| Integrate a client | [MCP and named-pipe protocol](docs/PROTOCOL.md) |
| Understand the implementation | [Architecture](docs/ARCHITECTURE.md) |
| Publish directory listings as the owner | [Publishing guide](docs/PUBLISHING.md) |

## Limits to know

- One seat per Windows session: agents and CLI clients share it and take turns.
- Files, accounts, network ports and application singletons are shared. Steam games run in the seat
  using the Steam client on your desktop, which stays there, without the overlay or Steam Input; other
  single-instance apps may already belong to your main session. Use separate browser profiles for
  browser testing.
- Capture and input depend on Windows and the application. Use `anode capabilities` to check;
  a connected seat alone does not prove screenshots or input work.
- Protected video and exclusive fullscreen may not capture. Without [HidHide](https://github.com/nefarius/HidHide/releases),
  virtual gamepads also reach games on your main desktop.

## Privacy Policy

Anode sends nothing to its maintainers: the runtime has no telemetry, update check or remote
endpoint. Screenshots and UI text go only to the MCP client or CLI that asked for them, and that
client may send them to its model provider. Read the [privacy policy](docs/PRIVACY.md).

## Build and contribute

Install the **.NET 10 SDK**, then:

```powershell
git clone https://github.com/skulitom/Anode.git
cd Anode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest -Package
```

The executable and docs are in `dist`; the downloadable bundle and checksums are in
`artifacts\release`. Quick checks require no seat and send no desktop input.
[Contributing and release process](CONTRIBUTING.md).

Found a problem? [Report a bug](https://github.com/skulitom/Anode/issues/new?template=bug_report.yml)
or [suggest an improvement](https://github.com/skulitom/Anode/issues/new?template=feature_request.yml).

## Credits and license

Built on documented Windows [child sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions),
the [Remote Desktop ActiveX control](https://learn.microsoft.com/en-us/windows/win32/termserv/remote-desktop-activex-control)
and [Task Scheduler](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex).
Optional gamepad support uses [ViGEmBus](https://github.com/nefarius/ViGEmBus), and
[HidHide](https://github.com/nefarius/HidHide) keeps the virtual controllers inside the seat.
Related projects: [Cathode](https://github.com/skulitom/CathodeDisplay),
[LibreAutomate](https://github.com/qgindi/LibreAutomate) and [BetterGI](https://github.com/babalae/better-genshin-impact).

[MIT license](LICENSE).
