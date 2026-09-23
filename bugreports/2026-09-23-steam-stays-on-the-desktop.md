# A game in the seat can use the Steam client on the desktop

The user wanted Steam to stay on their desktop while agents play Steam games in the seat. On
September 23 Steam was found in the seat, session 5, instead of the desktop, session 1. Steam was
not in the user's startup programs; the seat host's log showed `seat launched steam app 410340`,
an agent's Liftoff launch. Launching a Steam game in the seat meant starting `steam.exe` there, and
a Steam client started in one session takes over from the client in another, because Steam keeps
one client per Windows user.

## Why games could not reach the desktop client

The [September 14 probe](2026-09-14-direct-steam-launch-disrupts-parent.md) found that Liftoff's
`steam_api64.dll` in the seat reported "Cannot create IPC pipe to Steam client process" while Steam
ran on the desktop, including with `steam_master_ipc_name_override=Steam3Master`. That override
names the client's default objects and cannot reach another session.

A read-only listing of the object namespace showed what Steam's client uses. Its handshake objects
are `Steam3Master_SharedMemFile` (a section) and `Steam3Master_SharedMemLock` (an event), created
without a prefix and so kept in the session's own `\Sessions\<id>\BaseNamedObjects`. Their security
descriptors grant everyone access, and `steamclient64.dll` duplicates the handles for each later
pipe into the game process, which works across sessions for the same user. Only the names are bound
to a session. The Steam Client Service's objects live in the global namespace. Steam's client
refuses a backslash in the override name, so `Session\1\Steam3Master` cannot be used directly.

## Experiments

All ran from the desktop, session 1, against the Steam client that was then running in the seat,
the mirror of the intended direction. No game or Steam client was started or closed, and Liftoff,
which an agent was running in the seat, kept running.

1. With probe-named events only, an ordinary process created an object-manager symbolic link in its
   own session's namespace, pointing at an event in another session. A plain Win32 name opened the
   same event through it.
2. With the user's approval, a probe loaded Steam's `steamclient64.dll`, created such links to the
   seat's two handshake objects under a probe name, set the override to that name and asked for a
   Steam pipe. Without the links it got none. Through them it got one, `SteamUtils010` answered with
   the public universe and the current server time, and the pipe was released. No app id was used,
   so no game appeared as running.

## Change

`steam.launch` starts the game's own program in the seat with `SteamAppId`, `SteamGameId`,
`SteamOverlayGameId` and the override naming links the seat host keeps to the client's objects. The
program, arguments and working folder come from Steam's launch configuration in `appinfo.vdf`; a
read-only check resolved Liftoff, Half-Life 2 and Counter-Strike 2 on this machine as Steam does.
When Steam is not running, the seat host starts it on the desktop through the Task Scheduler and
waits for it to sign in. A client already in the seat, or `force`, keeps the old launch through it.

Quick checks cover the configuration parsing, every launch path against stand-ins and the links
between probe objects.

## Live test

The same evening, with the agents paused, the user's seat was closed, which also closed its Steam
client, and a dev seat started in session 6. With Steam not running, `anode steam 410340` took 18
seconds. The seat host started Steam in session 1 through the Task Scheduler, linked its names, and
started Liftoff in session 6 with the bridge's environment. Steam stayed in session 1.

Liftoff reported that `SteamAPI_Init()` failed. Its environment was right. Steam's connection log
showed the client signing the account in at 22:10:56 and connecting at 22:10:57, the second Liftoff
initialized, while the launch had counted Steam as signed in at 22:10:37. The `ActiveUser` value
Steam keeps under `HKCU\Software\Valve\Steam\ActiveProcess` still held the account of the client
closed with the seat. Two minutes later, a probe in session 6 calling Liftoff's own `steam_api64.dll`
with the same environment initialized, and Steam's console log listed it as a game process of app
410340.

A launch now asks the client instead: `anode __steam-ready`, run in the seat through the bridge with
no app id, creates a pipe, connects the global user and asks `ISteamUser::BLoggedOn`. Without an app
id Steam does not list it as a game. In session 6 it answered "online" through the bridge and
"unavailable" for a name with no client behind it.

The fixed launch then ran end to end, from a new seat in session 7, after Steam had exited cleanly.
`anode steam 410340` returned after 39 seconds. Steam started in session 1 at 23:51:36, and Liftoff
started in session 7 at 23:52:15, once the check answered "online" and five seconds more had passed.
Three seconds later Steam's console log listed Liftoff.exe as a game process of app 410340, Steam's
`RunningAppID` became 410340, and Liftoff's main menu showed the user's Steam profile. Liftoff logged
no Steam error. Steam stayed in session 1 throughout.
