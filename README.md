# Anode — a background Windows desktop for AI agents

Give an agent its own **screen, mouse pointer and keyboard focus** while you keep using your PC.
Anode runs applications in a Windows child session and exposes them through a CLI and a
**Model Context Protocol (MCP) server** for Codex, Claude Code and other MCP clients.

[![Release](https://img.shields.io/github/v/release/skulitom/Anode)](https://github.com/skulitom/Anode/releases/latest)
[![Windows build](https://github.com/skulitom/Anode/actions/workflows/build.yml/badge.svg)](https://github.com/skulitom/Anode/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Download for Windows x64](https://github.com/skulitom/Anode/releases/latest/download/anode-windows-x64.zip)**
· [Installation](docs/INSTALL.md) · [Connect an agent](docs/CONNECTING-AGENTS.md)
· [Troubleshooting](docs/TROUBLESHOOTING.md) · [Changelog](CHANGELOG.md)

## What can you do with it?

- **Develop and test apps:** run builds and local servers, collect command output, inspect native
  controls, test browser forms and capture screenshots in the background desktop.
- **Give an agent a desktop:** 30 MCP tools for screenshots, mouse/keyboard input, accessibility,
  UI waits, application launches and cancellable command jobs.
- **Watch when you want:** show the viewer with `anode show`; use `anode hide` to keep working.
- **Stop the seat:** press **Ctrl+Alt+Shift+K** or run `anode kill`. This signs the child session out
  and closes every application in it, including unsaved work.
- **Automate games:** optional virtual Xbox 360 controller support through ViGEmBus.

A child session has its own pointer and focus; adding a virtual monitor to your current session
does not. The seat runs as your Windows user and shares your files, account and network.
**This is desktop isolation, not a security sandbox.** Virtual gamepads are machine-wide.
See [security and isolation](docs/SECURITY.md).

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
`%LOCALAPPDATA%\Programs\Anode`, and adds that folder to your user PATH. **Open a new terminal**
afterward. Machine setup and agent registration are separate steps below.

Prefer portable? [Download the ZIP](https://github.com/skulitom/Anode/releases/latest/download/anode-windows-x64.zip),
extract the entire folder, and use `.\anode.exe` in place of `anode`.
Builds are currently unsigned; Windows may show a SmartScreen warning.
[Custom locations, offline installation, updates and removal →](docs/INSTALL.md)

## Set up and connect

```powershell
anode doctor | Out-Host           # reports prerequisites; no changes
anode setup | Out-Host            # one administrator prompt, once per machine
anode configure | Out-Host        # detects installed Codex / Claude Code CLIs
```

`setup` enables Remote Desktop and child sessions. It adds no firewall rules, but can restart
Remote Desktop Services and disconnect existing RDP sessions.
[See exactly what changes](docs/SECURITY.md#what-anode-setup-changes).

`configure` backs up client settings, registers Anode by absolute path and sets a 420-second tool
timeout. Use `anode configure codex` or `anode configure claude` to choose one client.
**Restart your agent session** to load the tools. Claude Desktop and other clients use
[this MCP configuration](docs/CONNECTING-AGENTS.md#3-any-other-mcp-client).

## Try it

Start and check the background seat:

```powershell
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

If Windows Hello/PIN sign-in prevents automatic login, use `anode start --hidden --sign-in` to
request the native Windows credential dialog. See [sign-in troubleshooting](docs/TROUBLESHOOTING.md).

Then ask your agent:

> Use Anode to open Notepad in the background seat, type "Hello from Anode", and show me a
> screenshot. Keep the viewer hidden and leave my main desktop available.

Or work through the CLI:

```powershell
anode exec --cwd C:\project --wait 1000 -- dotnet build | Out-Host
anode windows | Out-Host
anode shot seat.png | Out-Host
```

PowerShell users: piping to `Out-Host` makes PowerShell wait for this GUI executable and display
its output. Scripts should check `$LASTEXITCODE` after each command.

## Documentation

| I want to… | Start here |
| --- | --- |
| Install, update or uninstall | [Installation guide](docs/INSTALL.md) |
| Connect Codex, Claude Code or another MCP client | [Connecting agents](docs/CONNECTING-AGENTS.md) |
| Run apps, control the viewer or use a gamepad | [Usage guide](docs/USAGE.md) |
| Test native apps and browsers | [Development and testing](docs/DEVELOPMENT-TESTING.md) |
| Inspect windows and accessible controls | [Desktop tools](docs/DESKTOP-TOOLS.md) |
| Understand permissions and isolation | [Security](docs/SECURITY.md) |
| Diagnose startup, sign-in or capture problems | [Troubleshooting](docs/TROUBLESHOOTING.md) |
| Integrate a client | [MCP and named-pipe protocol](docs/PROTOCOL.md) |
| Understand the implementation | [Architecture](docs/ARCHITECTURE.md) |

## Limits to know

- One connected child session per machine. Agents and CLI clients share that seat.
- Files, accounts, network ports and application singletons are shared. Steam or another
  single-instance app may already belong to your main session; Anode refuses unsafe direct Steam
  launches. Use separate browser profiles for browser testing.
- Capture and input depend on Windows and the application. Use `anode capabilities` to check;
  a connected seat alone does not prove screenshots or input work.
- Protected video and exclusive fullscreen may not capture. Virtual gamepads can also affect
  games on your main desktop.

## Build and contribute

Install the **.NET 8 SDK**, then:

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
Optional gamepad support uses [ViGEmBus](https://github.com/nefarius/ViGEmBus).
Related projects: [Cathode](https://github.com/skulitom/CathodeDisplay),
[LibreAutomate](https://github.com/qgindi/LibreAutomate) and [BetterGI](https://github.com/babalae/better-genshin-impact).

[MIT license](LICENSE).
