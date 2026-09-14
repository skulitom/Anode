# Troubleshooting

Start here:

```powershell
anode doctor      # the machine settings a seat needs
anode selftest    # the parts of Anode that do not need a seat
anode status      # what a running seat is doing
```

The log is at `%LOCALAPPDATA%\Anode\anode.log`. Every role writes to it with a timestamp and a tag
(`daemon`, `seat`, `cli`, `mcp`), so a failed bring-up leaves a trail across both sessions.
Detached launches pass the resolved state directory to the daemon and seat host. `anode status --json`
includes `logPath` and `logError`; a non-null `logError` gives the actual write failure. The first write
failure also goes to stderr. A diagnostic path can be selected with `anode start --state-dir C:\AnodeLogs`.
Packaged launchers can redirect AppData into their package's `LocalCache` directory. Anode resolves
the physical directory before handing it to Task Scheduler, so both processes use the same file.
Use the `logPath` reported by status when locating a scheduled daemon's log.

---

## The seat will not come up

### A pipe connection reports access denied

Run Anode and its CLI/MCP clients as the same Windows user, from unelevated terminals.
The pipes check identity and elevation level; an administrator terminal is not interchangeable
with an ordinary terminal. Only `anode setup` needs administrator approval.

### A command timed out and the viewer is still connected

The timed-out command may have executed. Check the seat before repeating it. Run `anode start`
to reconnect the seat host if the daemon reports it is not ready; Anode can recover that connection
without waiting for the viewer to reconnect. `anode kill` remains available to stop the seat.

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

Two causes.

**You sign in to Windows with a PIN.** PIN/Windows Hello authentication has limitations in child
sessions. To request credentials for the seat while leaving your current desktop signed in, use:

```powershell
anode start --sign-in
```

If Anode is already running, `anode quit` first; that closes programs in its seat. The new daemon
shows the native Windows credential dialog. Enter your account password there; the PIN may not
work. Anode does not retrieve or save the password and disables the dialog's save-credentials
option. `--sign-in` cannot be combined with `--hidden`. Windows may request credentials again
when you create another seat. Anode waits for Windows to complete login before launching its host;
a connected viewer or reserved child-session ID alone does not mean sign-in has succeeded.
Ordinary starts disable Windows credential prompting; use `--sign-in` only when you intend to
interact with the dialog. Stop requests viewer disconnection even if Windows logoff fails.

Microsoft documents the PIN
limitation for [picture-in-picture child sessions](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip#limitations-of-child-session-mode).

**Credential delegation is disabled by policy.** Check:

```
Computer Configuration → Administrative Templates → System → Credential Delegation
  → Allow delegating default credentials
```

If it is `Disabled`, the seat will prompt. Setting it to `Not Configured` restores the normal
behaviour. Until then, the workaround is to keep the seat up between tasks instead of stopping and
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

### "the seat came up but its agent never answered"

The child session exists but the seat host never served its pipe. Look for `[seat]` lines in the log.
The usual causes:

- The executable was moved or deleted between the daemon starting and the seat coming up. The daemon
  launches the seat host by full path.
- Antivirus blocked a process started by the Task Scheduler.
- The seat is still starting Explorer on a slow first sign-in. Anode waits 90 seconds; a very cold
  first seat can exceed that. Try `anode kill` then `anode start` again, which is faster the second
  time because the profile is warm.

---

## Programs in the seat

### Steam opens the game on my screen instead

Steam allows one instance per Windows user, and the seat is the same user. Whichever session Steam
started in owns the games.

```powershell
anode steam status     # says which case you are in
```

If you need Steam on your main desktop, leave it there. Starting another client in the seat can
disrupt the existing one. Moving Steam into the seat is an optional tradeoff: explicitly close it on
your desktop, then `anode steam <appid>` starts it in the seat. You lose main-desktop client access.
The `force` option does not provide two independent clients.

### A browser will not start in the seat

Chrome, Edge and Firefox refuse a second instance on the same profile directory. Give the seat its
own:

```powershell
anode run "C:\Program Files\Google\Chrome\Application\chrome.exe" --user-data-dir=C:\Users\you\AppData\Local\AnodeChrome
```

### Two copies of my tray apps appear

Everything in your `Run` key and Startup folder launches when the seat signs in, because the seat is
your account signing in. Nothing is broken; it is what a second sign-in means. Remove what you do not
want from startup, or stop the seat when you are not using it.

### The game shows a black screen in the viewer

Exclusive fullscreen and protected video do not capture through the remote pipeline. Switch the game
to **borderless windowed**. This affects both the viewer and `seat_screenshot`.

---

## Performance

### Hidden viewer capture fails

The seat can be `ready` while its display cannot be captured. RDP may suppress
rendering when its client is minimized; a hidden ActiveX viewer can also leave
`Graphics.CopyFromScreen` failing with Win32 error 6 (invalid handle). A locked or
unavailable desktop can produce a similar failure.

Anode 0.2 sets the per-user RDP `RemoteDesktop_SuppressWhenMinimized` preference to
`2` before creating its viewer, and turns viewer minimization into hiding to the
tray. Check `anode rendering` or `status.backgroundRenderingConfigured`. Existing
daemons must be restarted to pick up the new startup behaviour. Restart only after
saving seat work and arranging any required sign-in: `anode quit` normally signs
the seat out and closes its applications.

The setting is based on RDP client behaviour documented by
[SmartBear](https://support.smartbear.com/testcomplete/docs/testing-with/running/via-rdp/in-minimized-window.html).
It is a mitigation, not a guarantee that every hidden ActiveX viewer can capture;
verify with a fresh seat screenshot before sending visual input. Its effectiveness
on the reported machine remains unverified until the updated daemon is started.

`anode inspect <windowId>` reads accessible controls without a screenshot.
`seat_observe` can return those controls alongside a `screenshotError`; capture
failure does not require opening the viewer or using foreground Computer Use.
Games exposing only a rendered canvas still need working screenshots. See
[Desktop tools](DESKTOP-TOOLS.md) and [rendering preference scope/undo](SECURITY.md#per-user-background-rendering).

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

Lower the seat resolution, which helps more than anything else:

```powershell
anode up --width 1280 --height 720
```

---

## Stopping things

### The seat is frozen

Any of these work regardless of what the seat is doing:

- `Ctrl+Alt+Shift+K` from anywhere
- the **Stop seat** button, or the tray menu
- `anode kill`

They sign the child session out, which force-terminates every process in it. If the sign-out itself
fails, Anode kills the session's processes directly and says so in the log.

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

### My real controller and the virtual one fight

A ViGEm pad is machine-wide, so a game on your own screen sees it too. Detach it when the agent is not
using it (`anode gamepad detach`), or stop the seat, which detaches automatically.

---

## Undoing everything

```powershell
anode kill            # stop any seat
anode setup --undo    # turn child sessions back off
```

Remote Desktop is deliberately left enabled, because other software may now depend on it. Turn it off
in **Settings → System → Remote Desktop** if you want it off.

Anode stores nothing but a log. Delete `%LOCALAPPDATA%\Anode` and the executable and it is gone.
