# Security

Anode gives an autonomous agent a real Windows session running as you. That is the point, and it is
also the whole risk. This page states plainly what changes on your machine, what the seat is and is
not isolated from, and how to stop it.

## What `anode setup` changes

Four settings, all documented and reversible by hand, applied only after you approve one UAC prompt.
Decline the prompt and nothing changes. `anode setup --undo` reverts child sessions only.

| Setting | Key | Default | Anode sets | Why |
| --- | --- | --- | --- | --- |
| Remote Desktop host | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` → `fDenyTSConnections` | `1` | `0` | A child session is a loopback RDP connection. Without a host there is nothing to connect to. |
| Child sessions | `WTSEnableChildSessions(TRUE)` | off | on | The documented switch for the feature. |
| Frame cap (`--fps 60`) | `...\Terminal Server\WinStations` → `DWMFRAMEINTERVAL` | unset (30 fps) | `15` (60 fps) | Remote sessions are capped at 30 fps otherwise. |
| GPU (`--gpu`) | `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services` → `bEnumerateHWBeforeSW` | unset | `1` | Lets the seat render on a real GPU instead of the software adapter. |

Setup also starts `TermService`, or restarts it when the host setting changed or its loopback
listener is missing. Restarting can disconnect existing Remote Desktop sessions. Previously running
dependent services are restored. Setup verifies the listener and reports failure when it remains
unavailable; it does not repair certificate stores or change system crypto-folder permissions.

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

`--undo` first signs out any running seat, closing its programs. It does not remove the optional
`--fps 60` or `--gpu` values. If you set them, remove them from an elevated PowerShell and reboot:

```powershell
Remove-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations' -Name DWMFRAMEINTERVAL
Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -Name bEnumerateHWBeforeSW
```

Leave `bEnumerateHWBeforeSW` alone if your organization's Group Policy sets it.

## Per-user background rendering

Before creating its RDP viewer, the daemon sets the DWORD
`HKCU\Software\Microsoft\Terminal Server Client\RemoteDesktop_SuppressWhenMinimized`
to `2`. This affects other RDP clients launched by the same user as well as Anode.
It changes rendering behavior, not authentication or firewall settings. Anode saves
the previous value in `rdp-rendering-backup.json` in its state directory. Unexpected
registry types are left unchanged and logged. To restore the saved value after
stopping Anode, run `anode rendering --restore` with the same `--state-dir` if used.
The next daemon startup configures rendering again unless `--no-background-rendering`
is supplied. A configured value alone does not prove that capture works.

## Desktop inspection

Window enumeration and UI Automation execute inside a worker that first verifies
its child-session identity with the parent daemon. Every target is checked against
the worker's session, process identity and observed control identity. Actions never
fall back to the parent desktop. Workers have a deadline and are terminated if an
app's accessibility provider hangs; an interrupted action may already have happened.

App titles and control text are untrusted content. Reports HTML-escape those values,
and terminal summaries strip control characters. Password control values are omitted
and password element actions are refused. Other visible text and screenshots may
contain private information; reports remain local until you choose to share them.

## What the seat is isolated from

| | |
| --- | --- |
| Your mouse pointer | **Isolated.** The seat has its own. The viewer is the one component on your desktop that could move yours (the Remote Desktop control applies the seat's programmatic cursor moves locally), so the daemon gates that call: it only passes while you have taken control in a visible, focused viewer. Check `pointerGuard` in `anode status --json`. |
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

## Agents, leases and command jobs

Desktop leases coordinate agents sharing one Windows identity; agent IDs are cooperative labels,
not authenticated principals. A same-user program can impersonate an ID or use native Windows APIs.
Lease ownership does not restrict filesystem/network access or autonomous application behavior.
Command job APIs enforce their recorded agent ID for discovery, reads and cancellation. See
[multiple agents](MULTI-AGENT.md) for scope and emergency Stop behavior.

`seat_exec` adds noninteractive command execution with captured output. The command runs as the
seat user, with inherited environment and shared files/network access. A Windows job owns only its
new worker and descendants, with hard lifetime/output limits. Output may contain application secrets;
the request, argument values and environment overrides are not logged. Jobs are not a filesystem or
network sandbox. Do not restart the seat host while an agent depends on a running build/server.

`scripts/repair-seat-input.ps1` is an opt-in administrator repair for a stuck GameInput helper.
It uses Windows' child-session API to verify the caller's child, checks the requested process's name,
session and relationship to the running GameInput service, then stops only that helper. It does not
stop or reconfigure either machine-wide GameInput service. `-WhatIf` verifies the target without
stopping it. The service may recreate a helper; this is not an automatic recurring repair.

- Give it the **MCP server**, not a shell in your session. The tool surface is deliberately narrow.
- The daemon and an agent share one seat. **Your stop button always wins**, because the daemon owns
  the lifecycle and the agent is a client. Over MCP, only `seat_start` and `seat_lease` acquisition
  start a seat; other tools report that Anode or its seat is not running instead of bringing back
  a seat you stopped.
- Watch the log at `%LOCALAPPDATA%\Anode\anode.log`. Every launch, stop and failure is recorded with a
  timestamp and a role.
- The CLI/MCP runtime has no telemetry, update check or remote control endpoint. The seat uses
  loopback RDP. The separately invoked installer downloads releases and checksums from GitHub;
  offline installation is available. Agent registration uses the installed clients' CLIs.

## Reporting a problem

Open an issue at https://github.com/skulitom/Anode/issues. If it is a security problem, please say so
in the title and leave out anything sensitive; a maintainer will follow up on how to share details.
