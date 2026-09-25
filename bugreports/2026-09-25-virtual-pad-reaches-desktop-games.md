# An agent's virtual controller steered the user's own game

Status: fixed with HidHide's session jail and verified live on September 25 after HidHide 1.5.230
was installed and the machine restarted.

## What happened

On September 24 at 19:23 an agent was flying Liftoff in the seat (session 2) with Haltere's flight
controller while the user played No Man's Sky on the desktop (session 1). The user wrote "I think you
are controlling my game by accident"; the agent stopped its controller. The pad was not Anode's: the
flight controller plugs in its own ViGEm pad through `vgamepad`, from a `seat_exec` job. The next day
the agent added a guard to Haltere that unplugs the pad whenever a game runs outside the seat, which
also stops the flight whenever the user plays. The user wants agents to keep using controllers while
they play, without disturbance.

## Why

A ViGEm pad is a device on the machine. XInput (`xinput1_4.dll` on this machine calls
`DeviceIoControl` on the device from the game's own process), DirectInput, raw input and Steam's
controller support all open the pad's devices from whatever session they run in. Windows has no
per-session device, and ViGEmBus lets only the client that plugged a pad in unplug it, so Anode could
neither hide nor remove another program's pad.

## Options considered

| Option | Verdict |
| --- | --- |
| Unplug while a game runs on the desktop | Implemented first (Haltere did the same); rejected by the user, since it stops the agent. |
| Device security descriptors denying `CONSOLE LOGON` (S-1-2-1, in desktop tokens, absent from the seat's) | Needs an administrator for every pad; the HID device's name changes on every plug-in. |
| Hooking XInput in games launched in the seat | Injects into games; anti-cheat risk; one hook per input API. |
| **HidHide session jail** | Chosen. HidHide 1.4.186 and later (the public release is 1.5.230) fail every open of a listed device except by the System process, allowed programs and, for an entry `path!n`, programs in session n. It filters the Xbox 360 class (XInput) and the HID class, and its control device lets any user change the list. |

## Change

`PadIsolation` in the seat host jails every device of each seat pad to the seat's session. HidHide
checks opens, not handles already open, so names are listed before a pad takes them:

- ViGEm names a pad after the lowest free serial (`USB\VID_045E&PID_028E\01`), so the next four free
  names are listed ahead of time.
- Windows names the pad's HID child after a counter that restarts at boot (`IG_00`, `IG_01`, ...). The
  device registry keeps every name each pad name's children have had; serial 01 on this machine had 88
  (counter values 00-2B, two devices each), and all of them are listed with the pad name. A name
  Windows has never used before is listed when the arrival notification comes, so a desktop program
  that opens HID devices on arrival could open that one first.
- Anode's own pads always belong to the seat; other pads plugged in while the seat runs do too, unless
  a ViGEm program runs outside the seat (by name, or with ViGEm's client module loaded). Pads present
  before the seat started are never listed.
- After Anode plugs a pad in, the daemon, in the user's session, tries to open its XInput and HID
  interfaces. If it can, the pad is unplugged again and the attach fails with `not_isolated`.
- Anode touches only entries ending in `!` and a session number for pad devices, and switches HidHide
  on only when nothing else is listed. The daemon removes a seat's entries after signing it out and
  any ended session's entries when it starts.

## Verification so far

- `selftest --quick`: list round trips, jail parsing, reserved names, the user's entries and settings
  left alone, ended seats cleared, ViGEm programs outside the seat, attach with a made-up device tree
  and stand-in controllers, including a desktop that can still open the pad.
- Read-only on this machine: the device tree queries found the 88 recorded names under serial 01,
  among them `HID\VID_045E&PID_028E&IG_07\3&76c8945&0&0000` of the pad plugged in on September 25 at
  15:18; HidHide was reported missing, and `doctor` warns about it.

## Live verification

After the restart HidHide was an upper filter of the HID, Xbox 360 and Xbox One device classes and
switched off with empty lists.

- The "seat-only gamepad" check plugged a neutral pad into `USB\VID_045E&PID_028E\01`, jailed to an
  unused session: XInput and HID opens from the desktop were refused. Anode switched HidHide on, and
  its entries were gone afterward.
- A seat started from the installed build (session 2) logged "HidHide is active". `gamepad attach`
  returned `seatOnly: true, verifiedFromDesktop: true`. The same probe ran on both sides: on the
  desktop XInput reported no controller and both the pad's XInput device and its HID device refused
  the open with error 5; in the seat the pad was in XInput slot 0 and both devices opened.
- The same held for a pad `vgamepad` plugged in from a job in the seat, the way Haltere's flight
  controller does: `gamepad state` reported it with owner `seat`, `seatOnly` and `verifiedFromDesktop`.
- Stopping the seat made the daemon remove the 100 entries it had kept (four free names for two pad
  products, with every recorded device name under serials 01 and 02).
- Each pad's HID device was `IG_00`: the counter starts again once no Xbox 360 pad is left, so pads
  plugged in one at a time keep names listed ahead of time.

Haltere's own guard, which unplugged its pad whenever a game ran, now stands down once Anode reports
the pad kept in the seat and checked from the desktop, and its flight preflight lists running games
instead of refusing.
