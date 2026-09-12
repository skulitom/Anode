# Security

Anode gives an autonomous agent a real Windows session running as you. That is the point, and it is
also the whole risk. This page states plainly what changes on your machine, what the seat is and is
not isolated from, and how to stop it.

## What `anode setup` changes

Four settings, all documented, all reversible, applied only after you approve one UAC prompt.
Decline the prompt and nothing changes.

| Setting | Key | Default | Anode sets | Why |
| --- | --- | --- | --- | --- |
| Remote Desktop host | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` → `fDenyTSConnections` | `1` | `0` | A child session is a loopback RDP connection. Without a host there is nothing to connect to. |
| Child sessions | `WTSEnableChildSessions(TRUE)` | off | on | The documented switch for the feature. |
| Frame cap (`--fps 60`) | `...\Terminal Server\WinStations` → `DWMFRAMEINTERVAL` | unset (30 fps) | `15` (60 fps) | Remote sessions are capped at 30 fps otherwise. |
| GPU (`--gpu`) | `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services` → `bEnumerateHWBeforeSW` | unset | `1` | Lets the seat render on a real GPU instead of the software adapter. |

**`fDenyTSConnections = 0` is the one to think about.** It enables the Remote Desktop listener on this
machine. Anode does **not** add a Windows Firewall rule, and Windows does not filter loopback, so a
child session works while inbound Remote Desktop from the network stays blocked by the firewall's
default deny. That is a meaningfully smaller change than "enable Remote Desktop" in Settings, which
also opens the firewall. It is not zero: if something else later opens the port, or the machine has a
permissive firewall profile, the listener is now there to be reached. Check with:

```powershell
Get-NetFirewallRule -DisplayGroup "Remote Desktop" | Select-Object DisplayName, Enabled, Profile
```

To undo:

```powershell
anode setup --undo    # turns child sessions off
```

It deliberately leaves Remote Desktop enabled, because other software on your machine may now depend
on it and silently disabling someone's RDP is worse than leaving a setting they can see. Turn it off
yourself in **Settings → System → Remote Desktop**, or set `fDenyTSConnections` back to `1`.

## What the seat is isolated from

| | |
| --- | --- |
| Your mouse pointer | **Isolated.** The seat has its own; nothing Anode does moves yours. |
| Your keyboard focus and foreground window | **Isolated.** |
| Your desktop and window list | **Isolated.** The seat's windows never appear on your monitors. |
| Your clipboard | **Isolated by default.** `--clipboard` shares it. |
| Drives, printers, ports, smart cards | **Not redirected**, by explicit setting. |
| Windows-key shortcuts | Go to you, not the seat, unless the viewer is full screen or you pass `--winkeys`. |
| Audio | **Muted by default.** `--audio` plays the seat's sound on your speakers. |

## What the seat is **not** isolated from

Read this list before you point an agent at it.

- **Your user account.** The seat signs in as you. It has your token, your profile, your
  `%USERPROFILE%`, your `HKCU`, your saved credentials, your browser profile directories, your SSH
  keys, your cloud-drive folders. **A seat is not a sandbox.** An agent in the seat can read and
  delete anything you can.
- **The file system and the network.** Full access to both, as you.
- **Other sessions' data at rest.** Separate sessions, one disk.
- **The virtual gamepad.** A ViGEm pad is a HID device on the **machine**, not in a session. Every
  session sees it, exactly like a controller plugged into USB. A game running on your own screen can
  read input the agent generates.
- **Machine-wide state.** Services, scheduled tasks, and anything else a standard user can change.
- **UAC.** The seat runs unelevated, and elevation prompts inside a child session are awkward at best.
  Do not plan on the seat installing software.

If you need a real boundary, put Anode inside a Windows Sandbox, a VM, or run it under a separate,
less privileged Windows user. Anode's isolation is about **attention**, not about **authority**.

## Stopping the seat

Four triggers, one path, and all of them work when the seat is wedged:

- **`Ctrl+Alt+Shift+K`** from anywhere on your desktop
- the **Stop seat** button in the viewer toolbar
- **Stop the seat** in the tray menu
- **`anode kill`**, or the `seat_stop` MCP tool

Each calls `WTSLogoffSession` on the child session, which force-terminates every process in it. If
that call fails, Anode enumerates the processes carrying that session id and kills them directly.

The seat also dies on its own when you sign out of Windows, because a child session is tied to its
parent session. Nothing survives your sign-out.

`anode ps kill` refuses to touch any process outside the seat's session id, so a confused agent
cannot reach across and kill something of yours.

## Running an agent in the seat

- Give it the **MCP server**, not a shell in your session. The tool surface is deliberately narrow.
- The daemon and an agent share one seat. **Your stop button always wins**, because the daemon owns
  the lifecycle and the agent is a client.
- Watch the log at `%LOCALAPPDATA%\Anode\anode.log`. Every launch, stop and failure is recorded with a
  timestamp and a role.
- Nothing in Anode talks to the network. There is no telemetry, no update check and no remote
  endpoint of any kind.

## Reporting a problem

Open an issue at https://github.com/skulitom/Anode/issues. If it is a security problem, please say so
in the title and leave out anything sensitive; a maintainer will follow up on how to share details.
