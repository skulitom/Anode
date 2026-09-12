# Troubleshooting

Start here:

```powershell
anode doctor      # the machine settings a seat needs
anode selftest    # the parts of Anode that do not need a seat
anode status      # what a running seat is doing
```

The log is at `%LOCALAPPDATA%\Anode\anode.log`. Every role writes to it with a timestamp and a tag
(`daemon`, `seat`, `cli`, `mcp`), so a failed bring-up leaves a trail across both sessions.

---

## The seat will not come up

### "Remote Desktop host: fDenyTSConnections = 1"

Run `anode setup`. Child sessions are loopback Remote Desktop, so the machine needs an RDP host even
though nothing goes over the network.

### "Child sessions: disabled"

Run `anode setup`. `WTSEnableChildSessions` needs an elevated token, which is why setup asks for one
administrator approval. Non-elevated callers get access denied.

### "Windows edition: Home"

Child sessions need Pro, Enterprise, Education or Server. Home has no Remote Desktop host and there
is no supported way around it.

### Disconnect reason 1800

The Remote Desktop host refused the loopback connection. Almost always Remote Desktop is still off or
child sessions were never enabled: run `anode doctor`. If both pass, check that the Remote Desktop
Services service is running:

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

**You sign in to Windows with a PIN.** A child session cannot use the PIN credential. Sign in to
Windows with your password once, then bring the seat up.

**Credential delegation is disabled by policy.** Check:

```
Computer Configuration → Administrative Templates → System → Credential Delegation
  → Allow delegating default credentials
```

If it is `Disabled`, the seat will prompt. Setting it to `Not Configured` restores the normal
behaviour. Until then, the workaround is to keep the seat up between tasks instead of stopping and
restarting it.

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

Close Steam on your desktop, then `anode steam <appid>`, which starts Steam inside the seat first.

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
