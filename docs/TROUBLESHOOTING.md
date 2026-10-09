# Troubleshooting

Start here, from an ordinary (not administrator) PowerShell window:

```powershell
anode doctor | Out-Host             # the machine settings a seat needs; changes nothing
anode selftest --quick | Out-Host   # Anode's own checks; no seat, capture or input
anode status | Out-Host             # what a running seat is doing
anode capabilities | Out-Host       # seat capture and known input blockers
```

`anode.exe` is a Windows GUI executable: in PowerShell, append `| Out-Host` to `anode` commands so
the prompt waits for their output and sets `$LASTEXITCODE`. Plain `anode selftest`, without
`--quick`, also captures your current desktop, runs a Task Scheduler check and attaches a
machine-wide virtual gamepad (with HidHide, also one it checks your desktop cannot open); run it only
when that is acceptable. `anode doctor --json` and
`anode status --json` print the same results as JSON. With nothing running, `status --json` prints
`"state": "stopped"` and `"daemonRunning": false` and exits 1. Every command and option is in the
[command reference](USAGE.md#command-reference).

The log is at `%LOCALAPPDATA%\Anode\anode.log`. Every role writes to it with a timestamp and a tag
(`daemon`, `seat`, `cli`, `mcp`), so a failed bring-up leaves a trail across both sessions.
Detached launches pass the resolved state directory to the daemon and seat host. `anode status --json`
includes `logPath` and `logError`; a non-null `logError` gives the actual write failure. The first write
failure also goes to stderr. A diagnostic path can be selected with `anode start --state-dir C:\AnodeLogs`.
Some desktop apps installed as MSIX packages, such as the Claude desktop app, redirect AppData for
the programs they start, so a daemon such an app starts keeps its log and state in
`%LOCALAPPDATA%\Packages\<app>\LocalCache\Local\Anode`. Anode resolves that physical directory before handing it to
Task Scheduler, so the app's own Anode processes and the scheduled daemon write to the same file.
Use the `logPath` reported by status when locating a scheduled daemon's log.

---

## The seat will not come up

### A pipe connection reports access denied

Run Anode and its CLI/MCP clients as the same Windows user, from unelevated terminals.
The pipes check identity and elevation level; an administrator terminal is not interchangeable
with an ordinary terminal. Only `anode setup` needs administrator approval.

### A command timed out and the viewer is still connected

The timed-out command may have executed. Check the seat before repeating it. A desktop action that
the seat stopped at its deadline says how far it got: `timed_out` with `started: false` did nothing,
since it was still waiting behind an earlier action; with `started: true` it was stopped partway.
Run `anode start` to reconnect the seat host if the daemon reports it is not ready; Anode can
recover that connection without waiting for the viewer to reconnect. `anode kill` remains available
to stop the seat.

### "Remote Desktop host: fDenyTSConnections = 1"

Run `anode setup`. Child sessions are loopback Remote Desktop, so the machine needs an RDP host even
though nothing goes over the network.

### "Child sessions: disabled"

Run `anode setup`. `WTSEnableChildSessions` needs an elevated token, which is why setup asks for one
administrator approval. Non-elevated callers get access denied.

### Remote Desktop is enabled but its listener fails

`doctor` probes TCP connections to the configured RDP port on loopback. A running `TermService` and
`fDenyTSConnections = 0` do not establish that the listener exists. `setup` starts a stopped service,
or restarts a running service after enabling the host or when its listener is missing. Restarting
the service may disconnect existing Remote Desktop sessions. Setup reports failure if the listener
still does not respond; it preserves a nonzero exit code when its elevated step fails.

If restarting does not help, inspect Event Viewer under
`Microsoft-Windows-TerminalServices-LocalSessionManager/Operational`, especially event 17.
`0x80070005` means access denied and can indicate a failure to create or access the Remote Desktop
certificate or private key. Inspect the certificate store and crypto-folder permissions; save the
existing ACL before any targeted repair. Anode setup does not change system crypto-folder ACLs.
Microsoft's [RDP troubleshooting guide](https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/rdp-error-general-troubleshooting)
also covers listener, certificate and key-folder checks. A TCP probe verifies reachability; it does
not prove certificate validity or successful sign-in.

### "Windows edition: Home"

Child sessions need Pro, Enterprise, Education or Server. Home has no Remote Desktop host and there
is no supported way around it.

### Disconnect reason 1800

Windows could not establish the child-session connection. Run `anode doctor` and check the listener
and child-session results. A disconnect during startup is recorded as `error`, with the numeric
reason in `lastError`, and startup returns that failure promptly. Check the service as well:

```powershell
Get-Service TermService
```

### Disconnect reason 516 or 520

Nothing is listening on the RDP port. Check `TermService`, and check whether the port was moved:

```powershell
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' PortNumber
```

Anode reads that value and connects to whatever it says.

### It asks for a password every time

Two causes. A failed automatic sign-in leaves the seat in `logon-error`, with the Windows error
code in `lastError`.

**You sign in to Windows with a PIN.** PIN/Windows Hello authentication has limitations in child
sessions. To request credentials for the seat while leaving your current desktop signed in,
press **Sign in…** next to **Reconnect** in the viewer header. If the viewer is hidden, choose
**Sign in…** from the Anode tray icon's menu, or open the viewer from the tray icon or with
`anode show`. This retries sign-in without restarting Anode or closing seat programs. An agent
cannot do this step: the dialog needs your password. When Anode is not running yet, you can also
request the prompt as it starts:

```powershell
anode start --sign-in
```

A running Anode refuses `--sign-in`; use **Sign in…** instead.

Anode shows the native Windows credential dialog. Enter your account password there; the PIN may not
work. Anode does not retrieve or save the password and disables the dialog's save-credentials
option. `--sign-in` cannot be combined with `--hidden`. Windows may request credentials again
when you create another seat. Anode waits for Windows to complete login before launching its host;
a connected viewer or reserved child-session ID alone does not mean sign-in has succeeded.
Ordinary starts disable Windows credential prompting; use **Sign in…** or `--sign-in` when you intend to
interact with the dialog. Stop requests viewer disconnection even if Windows logoff fails.

Microsoft documents the PIN
limitation for [picture-in-picture child sessions](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip#limitations-of-child-session-mode).

**Credential delegation is disabled by policy.** Check:

```
Computer Configuration → Administrative Templates → System → Credential Delegation
  → Allow delegating default credentials
```

If it is `Disabled`, the seat will prompt. Setting it to `Not Configured` restores the normal
behavior. Until then, the workaround is to keep the seat up between tasks instead of stopping and
restarting it.

### The listener passes but authentication fails

If Windows repeatedly rejects a Microsoft-account password, stop retrying the same dialog.
An administrator can inspect Security event 4625 for the corresponding attempt. Status
`0xC000006D` with substatus `0xC000006A` means the local password verifier rejected it;
it does not establish whether that password is valid for the online Microsoft account.
PIN/Hello-only sign-in and a stale local password cache are possible contributors, not
conclusions proved by that event. A separate local **Run as different user** check with
the same account can distinguish local credential acceptance from RDP-specific failure
without signing out the current desktop. Enter credentials only in Windows' own prompt.
Do not reset the account password or change authentication policies based solely on
Anode's generic RDP error.

An accepting TCP listener does not prove that Windows can authenticate a child session. Anode
reports the RDP control's native disconnect description when available. If Windows records a
terminal SSL handshake failure before delivering that callback, Anode reads event 226 in
`Microsoft-Windows-TerminalServices-RDPClient/Operational` and preserves its hexadecimal code in
`lastError`. Only events from the daemon process and the current connection attempt are used.

`0x80004005` is an unspecified Windows failure, not proof of a particular certificate or policy
problem. Check the sign-in method described above, then the certificate and authentication logs.
Avoid disabling authentication or weakening crypto-folder permissions as a generic workaround.

### A certificate warning appears

Child sessions go through the normal RDP client, which validates the machine's certificate chain. If
your machine has an incomplete chain, the connection fails. Make sure intermediate certificates are
present in the local machine's trusted authorities.

### The seat came up but its host never answered

The child session exists but the seat host, the `anode __seat-host` process Anode places inside it,
never served its pipe. While Anode waits for it, `anode status` shows the state `starting-agent`.
The error names the log file; look for `[seat]` lines in it. The usual causes:

- The executable was moved or deleted between the daemon starting and the seat coming up. The daemon
  launches the seat host by full path.
- Antivirus blocked a process started by the Task Scheduler.
- The seat is still starting Explorer on a slow first sign-in. Anode waits 90 seconds; a very cold
  first seat can exceed that. Try `anode kill` then `anode start` again, which is faster the second
  time because the profile is warm.

### Another Anode has the seat

`start` and `lease acquire` exit 3, and `seat_start` fails, with "The main Anode is running this
Windows session's seat" or another channel's name. Windows gives each Windows session one child
session, so one Anode at a time can run a seat in it. A development build (the dev channel, see
[a separate dev Anode](DEVELOPMENT-TESTING.md#a-separate-dev-anode)) will not start a seat while the
installed Anode runs one, and the installed Anode will not start one while a dev seat runs; neither
takes the other's seat over. Use the Anode that has the seat, or save work in its seat and quit that
Anode (`anode quit` from its own `anode.exe` closes every program in the seat), then try again. "Kept
its seat signed in" means that Anode quit with `--keep`: start it and quit it once to sign the seat
out. A dev Anode logs to `%LOCALAPPDATA%\Anode-dev\anode.log`.

---

## Desktop leases

Agents share one desktop through an exclusive lease. Only `anode start`, `anode up` and
`anode lease acquire` start Anode from the CLI; over MCP only `seat_start` and `seat_lease` with
`{"action":"acquire"}` do. Other commands report that Anode or its seat is not running instead
of bringing back a seat someone stopped.

These need a lease:

| Work | CLI | MCP |
| --- | --- | --- |
| Screenshots | `shot` | `seat_screenshot` |
| Windows and controls | `windows`, `inspect`, `window`, `element`, `wait` | `seat_windows`, `seat_observe`, `seat_window`, `seat_element`, `seat_wait` |
| Mouse and keyboard | `click`, `move`, `scroll`, `key`, `type` | `seat_click`, `seat_move`, `seat_drag`, `seat_scroll`, `seat_key`, `seat_type` |
| Programs and commands | `run`, `steam <appid>`, `ps kill`, `exec` | `seat_run`, `steam_launch`, `seat_kill_process`, `seat_exec` |
| Gamepad changes | `gamepad attach`, `detach`, `reset`, `tap`, `hold`, `release`, `stick` | `gamepad_attach`, `gamepad_detach`, `gamepad_reset`, `gamepad_tap`, `gamepad_set` |

`status`, `capabilities`, `ps`, `steam status`, `gamepad state`, `show`, `hide`, `control`,
`doctor` and `guide` need no lease. Your own job reads and cancellation (`job`, `jobs`,
`seat_job`) need only your agent ID. `kill` and `quit` need neither: they stop the seat for every
agent. In PowerShell:

```powershell
$env:ANODE_AGENT_ID = 'me'
$lease = anode lease acquire | Out-String | ConvertFrom-Json   # starts the seat if needed
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
# desktop commands; run `anode lease renew | Out-Host` before the lease expires
anode lease release | Out-Host
```

MCP agents call `seat_lease` with `{"action":"acquire"}`; the server remembers the token and
attaches it to desktop calls. See [multiple agents](MULTI-AGENT.md) for the whole model.

### "Acquire the desktop with seat_lease action=acquire" or "An agentId is required"

The command needs a lease, or an agent ID, that it did not receive. Run the snippet above in the
same shell, or pass both before the command: `anode --agent me --lease TOKEN shot seat.png`.

### "Anode is not running"

The CLI prints "Anode is not running. Start it with `anode start --hidden` (agents:
`anode lease acquire` starts it)." Desktop commands never start Anode themselves.

Over MCP, a desktop tool that finds no running Anode answers "Anode is not running or your lease
ended" and forgets any lease token it held: someone quit Anode, it exited, or no lease was acquired.
Lease-gated tools never start a seat, so a desktop a person closed stays closed. When the task
still needs a desktop, call `seat_lease` with `{"action":"acquire"}`, which starts a hidden seat,
and observe again before acting.

### "The seat is stopped"

Someone pressed **Stop the seat**, the Ctrl+Alt+Shift+K hotkey, `anode kill` or `seat_stop`. Anode
itself is still running, but the seat and every program in it are gone, and so is the lease. Over
MCP, diagnostics report `state: stopped` and desktop tools answer "The seat was stopped, which
ended your lease" and forget the token. Acquire again (`seat_lease` with `{"action":"acquire"}`, or
`anode lease acquire`), which starts a new seat, then observe afresh.

### "The desktop is busy with agent …" (`seat_busy`)

Another agent holds the lease. With `ANODE_AGENT_ID` set, `anode lease status` shows the owner and
the time remaining; MCP agents call `seat_lease` with `{"action":"status"}`. Wait for release or
expiry (default 120 s, at most 600 s); there is no queue or takeover. On release, `seat_busy` means
your own desktop action is still running; retry when it finishes. `anode kill` stops the whole seat
for every agent, including the owner's work.

### `lease_expired` or `stale_lease`

The lease was not renewed within its lifetime, or a newer acquisition replaced it. Acquire again and
observe afresh; do not replay uncertain input. For long tasks pass `--ttl` (up to 600 seconds) and
run `anode lease renew` before expiry; MCP agents renew with `{"action":"renew"}`. Release and expiry
also clear window and control references, held keys and buttons, and this host's virtual controllers.

---

## Programs in the seat

### A Steam game will not start in the seat

`anode steam <appid>` starts the game in the seat and connects it to the Steam client on your
desktop, so Steam never moves. Start with where Steam is:

```powershell
anode steam status     # where Steam runs, and whether it is signed in
```

- **"not ready for games"**: Steam is still starting or updating, or no account is signed in. When
  Steam was not running, Anode started it minimized; look for it in the notification area, sign in if
  it asks, then launch again. Anode asks Steam the way a game does before starting one, so a game it
  starts does not find Steam half ready.
- **The wrong program starts, or none is found**: pass the right one with `--exe` (MCP: `exe`),
  absolute or relative to the game's folder.
- **The game exits at once or says Steam is not running**: some games need Steam's overlay or are
  wrapped in Steam's DRM stub, and those only work when Steam itself starts them. `--force`
  (MCP: `force: true`) starts the game through a Steam client in the seat, which takes Steam over
  from your desktop; see below to bring it back.

### Steam left my desktop

A Steam client started in the seat takes Steam over from your desktop, because Steam keeps one
client per Windows user. Anode only does that with `--force`. It can also happen when something in
the seat starts `steam.exe` or a `steam://` link: a Steam entry in your startup programs, an older
Anode, or a game that restarts itself through Steam. `anode run` refuses those while Steam runs on
your desktop. To bring Steam back, exit it in the seat from its notification-area icon in the viewer,
or with `anode ps kill steam`, then start it on your desktop again.

### A browser will not start in the seat

Chrome, Edge and Firefox refuse a second instance on the same profile directory, so the browser on
your desktop keeps a browser in the seat off your usual profile. `anode run` refuses Chrome and Edge on
it while they run outside the seat. Use the seat's own profile, which persists between seats:

```powershell
anode browser https://example.com
anode browser --sign-in https://play.google.com/console   # from your own terminal: sign in for agents
```

Agents use `seat_browser`. For Firefox, pass `-no-remote -profile` with a folder of the seat's own.
The seat profile opens on one desktop at a time: close the window `--sign-in` opened before agents
use the seat's browser, and close the seat's browser before signing in again. While the other desktop
has the profile open, `anode browser` (agents: `seat_browser`) and `--sign-in` refuse and name the
browser process that has it. Chrome can keep running in the background with no window: end it with
`Stop-Process -Id <pid>`, and the seat's only once no agent needs it.
If `anode browser --sign-in` says the terminal runs inside a packaged app, run it from Windows
Terminal or PowerShell opened from the Start menu. See [Android apps and web consoles](ANDROID.md).

### An Android emulator will not start or stays black

- **Its screen stays black, or it exits at once.** Start it again with `--gpu software` (agents:
  `gpu: "software"`); the seat's graphics may not suit the emulator's default renderer.
- **It says the AVD is already running.** A writable start (`--writable`) cannot share an AVD; start it
  read-only, the default, or close the other emulator.
- **It says no hypervisor is available.** The emulator needs the Windows Hypervisor Platform or the
  Android Emulator hypervisor driver, as on your desktop; install one with Android Studio's SDK Manager.
- **"runs outside the seat".** That serial belongs to an emulator on your desktop or elsewhere, which
  Anode never drives. Start one in the seat and use its serial.
- **It did not finish booting in time.** A cold boot can take minutes. Check `anode android` until the
  seat's emulator says `booted`; its output is in the log file `android start` names.

### My own documents opened in the seat

Windows 11 Notepad, browsers on your usual profile and many editors restore your last session when
they start. In the seat they restore yours: open files, unsaved tabs and whatever those contain. The
seat is your account, so this is expected, not a leak between sessions. Agents should test with a
disposable app or a [separate profile](#a-browser-will-not-start-in-the-seat) instead. To close such
an app without touching your documents, close its window normally or end its process; Notepad keeps
unsaved tabs through both. Never answer its "Don't save" prompt for documents you did not create.

### Two copies of my tray apps appear

Everything in your `Run` key and Startup folder launches when the seat signs in, because the seat is
your account signing in. Nothing is broken; it is what a second sign-in means. Remove what you do not
want from startup, or stop the seat when you are not using it.

### My real pointer jumps while something runs in the seat

Games built on SDL (Half-Life, most Source and indie titles) call `SetCursorPos` whenever they enter
or leave relative mouse mode: opening a menu or the console, loading a level. Windows forwards that
to the Remote Desktop control in the viewer, which moves the pointer on the desktop it lives on,
yours, whenever your pointer is over the viewer's rectangle. It does this with the viewer view-only
and with the viewer hidden, because a hidden window still has a rectangle. The symptom is the pointer
teleporting to the same few spots, typically the point where the seat's center would be drawn.

Anode gates that call. Check that the gate is in place:

```powershell
anode status          # "your pointer  guarded; N seat pointer move(s) kept off your desktop"
anode status --json   # pointerGuard: { installed, patchedImports, suppressed, forwarded, viewer }
```

- `pointerGuard` missing: the daemon predates the guard. Restart it from a current build. That
  interrupts the seat, so arrange it with whoever has programs open there.
- `installed: false`: Anode could not install every required guard, or an import no longer points
  to it. New connections and reconnects are refused to protect your pointer. Update Anode and retry;
  if this continues, report the daemon log and the file version of
  `C:\Windows\System32\mstscax.dll`. This can happen if a Windows update changes the RDP client
  or Windows prevents the patch. Older Anode builds only logged the failure and could still connect.
- `suppressed` growing is the guard working. `forwarded` only grows while you have pressed
  **Take control** and the viewer is the foreground window; pointer moves inside the viewer are
  expected then, as in any Remote Desktop client.

`scripts/test-pointer-isolation.ps1` reproduces the condition on purpose and proves the pointer stays
put; see [Developing and testing](DEVELOPMENT-TESTING.md#reproducible-live-verification).

### The game shows a black screen in the viewer

Exclusive fullscreen and protected video do not capture through the remote pipeline. Switch the game
to **borderless windowed**. This affects both the viewer and `seat_screenshot`.

---

## Capture and input

### Hidden viewer capture fails

The seat can be `ready` while its display cannot be captured. RDP may suppress
rendering when its client is minimized; a hidden ActiveX viewer can also leave
`Graphics.CopyFromScreen` failing with Win32 error 6 (invalid handle). A locked or
unavailable desktop can produce a similar failure.

Anode sets the per-user RDP `RemoteDesktop_SuppressWhenMinimized` preference to
`2` before creating its viewer, and turns viewer minimization into hiding to the
tray. Check `anode rendering` or `status.backgroundRenderingConfigured`. A daemon
applies the preference only when it starts, so after `anode rendering --restore` or a
start with `--no-background-rendering`, restart it. Restart only after saving seat
work and arranging any required sign-in: `anode quit` normally signs the seat out
and closes its applications.

The setting is based on RDP client behavior documented by
[SmartBear](https://support.smartbear.com/testcomplete/docs/testing-with/running/via-rdp/in-minimized-window.html).
It is a mitigation, not a guarantee that every hidden ActiveX viewer can capture;
verify with a fresh seat screenshot before sending visual input. On the reported
machine, sustained hidden capture and changing browser screenshots were verified
after the updated daemon started. `anode capabilities` tests the current condition.

`anode inspect <windowId>` reads accessible controls without a screenshot.
`seat_observe` can return those controls alongside a `screenshotError`; capture
failure does not require opening the viewer or using foreground computer use.
Games exposing only a rendered canvas still need working screenshots. See
[Desktop tools](DESKTOP-TOOLS.md) and [rendering preference scope/undo](SECURITY.md#per-user-background-rendering).

### Capture works, but focus and input do not

Check `anode capabilities`. A SYSTEM-owned GameInput helper can hold the seat's
foreground window and prevent ordinary synthetic input from reaching applications.
Anode reports this known blocker explicitly. UIA and browser protocol actions may
still work, and `window ID raise` can make an app visible without claiming focus.

For this diagnosed fault, an administrator can run
`scripts/repair-seat-input.ps1 -HelperPid PID` from the parent session. The script
verifies Windows' child-session identity and stops only that child helper, preserving
the services and main desktop. It is not an automatic service change, and the helper
may return. From a source checkout, retest with `scripts/test-development.ps1 -VerifyInput`
to prove delivery.

### `seat_observe` fails with "Object reference not set to an instance of an object"

In 0.11.1 and earlier, `seat_observe` and `seat_wait` (CLI: `anode inspect` and `anode wait`) fail
this way on Windows 11 File Explorer windows. Explorer's command bar reports UI Automation's AppBar
control type, which .NET's UI Automation client has no name for, and one such control fails the whole
observation. A SemanticZoom control does the same, so other apps with either control can fail too. Use
`seat_screenshot` and `seat_click` (CLI: `anode shot` and `anode click`) on those windows. A fix for
the next release ([#20](https://github.com/skulitom/Anode/pull/20)) gives such controls the role
`Unknown`.

### Typed text arrives incomplete or out of order

**In 0.11.1 and earlier,** `seat_type` sends characters without a pause unless you pass `perCharMs`,
and it reports success even when the program lost some. In Chrome, of one 7,938-character text, about
the first 5,500 characters arrived intact; the rest had characters missing and out of order. Send long
text in several `seat_type` calls of at most about 2,500 characters each, with `perCharMs` 3, one at a
time, and check the field before you send the next piece. On the PC where this was measured, Windows
stretched each 3 ms pause to about 15.5 ms, so such a call takes about 40 seconds. Don't send more at
once: a call that runs past the 60-second request deadline reports a timeout while the seat goes on
typing, and Anode then shows the seat as `detached`
([what to do](#a-command-timed-out-and-the-viewer-is-still-connected)); never resend such a call. The
CLI can't do this in 0.11.1: `anode type` has no pacing option and types every word after `type` as
text. The pacing described below comes with the next release
([#18](https://github.com/skulitom/Anode/pull/18)).

A program reads typed characters at its own speed. `seat_type` (CLI: `anode type`) sends them 15 ms
apart and pauses while the focused program is busy, a pace Chrome keeps up with even in a long field.
If text still arrives wrong:

- **Type slower.** Pass a larger `perCharMs`. Use `perCharMs: 0` only for a program you know keeps up:
  it sends characters without a pause.
- **Send smaller pieces.** One call types at most about 3,000 characters at the default pace, so that it
  answers within the request's time. Longer text is refused before anything is typed; send it in
  several calls.
- **Skip the keyboard.** For a form field, `seat_element` `set_value` on the observed control sets the
  whole value at once.
- **`typing_stopped`.** The focused program stopped responding for 5 seconds, typing ran out of the
  call's time, or Windows refused the input partway. The error gives `typed` and `total` (a newline or an
  emoji counts as one character) and `nextIndex`, the UTF-16 index where the untyped rest starts. When
  `partlySent` is `true`, part of the character at `nextIndex` went out before the refusal: an Enter may
  have been pressed and not released, so it may already have taken effect, and Anode releases the key
  when the lease ends. Check the field before you send the rest; Anode never sends it again by itself.
- **Windows may not report every refusal.** Input an elevated window doesn't accept may be dropped
  without an error, so check the field after typing into one.
- **The seat stopped during a call.** The call ends with a lost connection or `typing_stopped`. Either
  way, stopping signs the seat out, which closes the program you were typing into, so don't send the
  rest.

### A display change fails or looks wrong

- **`display_not_applied`.** Windows applied a display other than the one asked for, most often a
  smaller scaling at a small resolution. The error's `result` is what the seat shows now, and
  screenshots and input already use it. Ask for high scaling on a larger resolution.
- **"The viewer could not reconnect".** Windows did not change the display live, and reconnecting the
  viewer at the new size failed. The seat and its apps keep running, and Anode reconnects the viewer at
  the display it had. If `anode status` then says `detached`, open the viewer with `anode show` and
  press **Reconnect**.
- **"would need the Windows password".** Windows did not change the display live, and this seat signed
  in through the credential dialog, so reconnecting the viewer at the new size would ask for your
  password in the middle of an agent's task. Anode left the viewer connected instead. When the seat's
  apps can close, `anode quit`, then `anode start --sign-in --width N --height N --scale N` starts a
  seat at the display you need.
- **An app is blurry or laid out for the old display.** It reads scaling only at startup, and Windows
  stretches it after a change, as on a real PC. Close it and start it again.
- **`display_restoring`.** The previous agent's lease changed the display and ended; Anode is putting
  back the startup display before anyone acts. Try again in a few seconds.
- **The display went back on its own.** Display changes last until the lease that made them ends, by
  release or expiry. Acquire the lease again and set the display again.

See [Test other displays](DISPLAYS.md).

---

## Performance

### Everything is choppy

Two settings, both needing a reboot:

```powershell
anode setup --fps 60 --gpu
```

`--fps 60` raises the remote-session cap from 30 fps. `--gpu` lets the seat render on your real
graphics adapter instead of the software one, which is the difference between a slideshow and a
playable game.

### It is still not fast enough for the game I want

A child session renders through the Remote Desktop graphics pipeline, not out of a display port. It is
good enough for most games at moderate settings and it is not good enough for competitive twitch play.
That is a property of the approach, not a bug to be fixed.

Lower the seat resolution below the default 1280x720, which helps more than anything else. While
you hold the desktop lease, `anode display 1024x576` lowers it in the running seat until the lease
ends. To start every seat that way, quit Anode first; `anode quit` closes every program in the seat,
so save work there:

```powershell
anode quit | Out-Host
anode start --width 1024 --height 576 | Out-Host
```

---

## Stopping things

### The seat is frozen

Any of these work regardless of what the seat is doing:

- `Ctrl+Alt+Shift+K` from anywhere
- the **Stop seat** button, or the tray menu
- `anode kill`

They sign the child session out, which force-terminates every process in it, for every agent;
desktop leases do not block them. If the sign-out itself fails, Anode kills the session's processes
directly and says so in the log.

### Ctrl+Alt+Shift+K does nothing

Something else registered that hotkey first. The log says so at startup. Use the toolbar button, the
tray menu, or `anode kill`.

### Windows will not restart while a seat is up

That is a Windows restriction on connected child sessions. `anode kill` first.

### I closed the viewer and the seat is still running

By design. Closing the window hides it to the tray so an agent's work survives you tidying your
desktop. Use **Stop seat** or `anode kill` to actually end it, and `anode show` or the tray icon to
watch it again.

---

## Gamepad

### "The ViGEm bus driver did not respond"

Install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) from the official releases page, not
from a repackaged mirror. The project is archived but the last signed release works on current
Windows 11. Everything else in Anode works without it.

### The game ignores the virtual controller

- Attach the pad **before** the game starts. Many games enumerate controllers once at launch.
- Some games only read the first XInput slot; `anode gamepad detach` any pad you left in another slot.
- Check the pad exists at all: Win+R, `joy.cpl`, in the seat.

### A game on my desktop reacts to the agent's controller

A ViGEm pad is a machine-wide device, so without help a game on your own screen reads it too. Install
[HidHide](https://github.com/nefarius/HidHide/releases) from its official releases page and restart when
its installer asks. From then on the seat keeps every virtual pad plugged in while it runs to itself:
games, Steam and the Xbox Game Bar on your desktop cannot open it, and you can play while an agent tests
a game in the seat. `anode doctor` reports it under **Controller isolation**, and `anode gamepad state`
shows each pad's `owner` and `seatOnly`.

- A pad from a program outside the seat stays shared. While such a program runs (DS4Windows, for
  example), only Anode's own controllers are kept in the seat, since a new pad could be that program's;
  `gamepad state` lists it under `otherViGEmPrograms`.
- A program on HidHide's application list can open hidden devices anywhere; `gamepad state` lists it
  under `allowedEverywhere`. Remove it from the list in HidHide's configuration if it should not.
- Without HidHide, detach the controller when the agent is not using it (`anode gamepad detach`), or
  stop the seat, which detaches automatically.

### "No controller was plugged in, because it would reach the user's desktop"

The attach failed with `not_isolated`: HidHide is installed but cannot keep the controller in the seat,
so Anode did not leave it plugged in. The message names the reason:

- **switched off**: HidHide is off and also hides devices of yours, so Anode will not switch it on.
  Switch it on in HidHide's configuration.
- **inverted**: HidHide's application list is inverted, which lets every program not on it see hidden
  devices. Turn that off in HidHide's configuration.
- **busy**: HidHide's configuration window or another program has it open. Close it and attach again.
- **the desktop could still open it**: HidHide listed the controller but your session could open it,
  usually because HidHide's restart is still pending. Restart Windows.

---

## Undoing everything

```powershell
anode quit | Out-Host                  # stop the seat, closing its programs, and exit Anode
anode rendering --restore | Out-Host   # put back your previous per-user RDP rendering value
anode setup --undo | Out-Host          # turn child sessions back off (one UAC prompt)
```

`setup --undo` also signs out a seat that is still running. Remote Desktop is deliberately left
enabled, because other software may now depend on it. Turn it off in
**Settings → System → Remote Desktop** if you want it off.

Anode leaves more than a log. Logs and the rendering backup stay in `%LOCALAPPDATA%\Anode`, or for a
daemon a packaged app started, in `%LOCALAPPDATA%\Packages\<app>\LocalCache\Local\Anode`. Run from your
own terminal without `--state-dir`, `anode rendering --restore` finds a backup in either. Also,
`setup --undo` does not remove the optional `--fps 60` and `--gpu` values
([how to remove them](SECURITY.md#what-anode-setup-changes)). The installer adds a user PATH entry,
a Start menu shortcut and an Installed apps entry, and `anode configure` adds agent registrations and
the `anode-desktop` skill. Uninstalling from Installed apps removes those for an installer-made copy
and offers the commands above; [Remove](INSTALL.md#remove) has the details and the manual steps.
