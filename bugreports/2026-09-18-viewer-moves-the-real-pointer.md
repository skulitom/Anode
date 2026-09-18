# The viewer moves the real mouse pointer when a seat program moves its cursor

Status: fixed in code and verified against the real `mstscax.dll` import table by the quick
self-test. The running 0.3.0 daemon was deliberately not restarted (other agents had programs open
in its seat), so `scripts/test-pointer-isolation.ps1` has not yet been run against a guarded daemon.
The unguarded daemon was measured with the same probe and watcher; those results are below.

## Report

Another project ran Half-Life in the seat (session 2) and sampled the main session's pointer at
250 Hz. Relative input injected into the seat with SendInput never reached the main pointer
(306,891 injected counts, pointer exactly still). The recording still held 61 teleports in 23
minutes. Half landed on one point: the seat's centre, (640,360) of 1280x720, as the viewer would
draw it on the main desktop, (1040,527) that day. The rest landed on the mapped position of wherever
the game restored its cursor. It happened with the viewer view-only, and with the viewer hidden: one
Esc tap in the seat teleported the main pointer.

## Cause

SDL calls `SetCursorPos` when it enters or leaves relative mouse mode (menu, console, level load).
The RDP server turns a programmatic cursor move into a server pointer-position update. The
MsRdpClient ActiveX control hosted by `Daemon/RdpViewer.cs` applies that update with `SetCursorPos`
on the desktop it lives on: the user's.

View-only never covered this. It is `EnableWindow(handle, false)`, which blocks input going to the
control, not pointer updates coming from the server. The control has no property for it either;
nothing on `IMsRdpExtendedSettings` was found that disables pointer-position updates.

`mstscax.dll` 10.0.26100.8162 imports `SetCursorPos` by name from USER32 as an ordinary import, not a
delay-load one. `SetPhysicalCursorPos`, `ClipCursor` and `mouse_event` are not imported. Six call
sites reference the import, all of the form `call qword ptr [slot]`; none caches the pointer.
`mstscax.dll` is the only Remote Desktop client module in the daemon process. `SendInput` is
imported as well. Nothing measured here points at it, and the live check below watches the pointer
itself rather than one API, so a second path would still fail it.

## What the control actually checks

Three runs against the unguarded daemon, viewer hidden and view-only. A probe in the seat alternated
`SetCursorPos` between (256,144) and (1024,576), then restored (640,360). A watcher in the main
session sampled `GetCursorPos` every 1-2 ms. Both are now `__cursor-probe` and `__pointer-watch`.

| Real pointer when the probe fired | Seat moves | Real pointer teleports |
| --- | --- | --- |
| inside the viewer's rectangle, then moved away by the user | 5 | 1, the first |
| on another monitor | 4 | 0 |
| inside the viewer's rectangle throughout | 5 | 5 |

In the last run every move leaked 1.5-2 ms after the seat call, landing on exactly seat + (400,167):
(656,311), (1424,743), (656,311), (1424,743), (1040,527). The last one is the reporter's point.

So the control applies a pointer update whenever the real pointer is geometrically over its window
rectangle. It does not check that the window is enabled, focused or even visible: a hidden window
still has a rectangle. That rectangle is 1280x720 of the primary monitor's work area by default,
which is why the teleports looked sporadic: they happened whenever the user happened to be working
in that region. It also means a test with the pointer elsewhere passes on a broken build.

## Fix

`Daemon/PointerGuard.cs` replaces `mstscax.dll`'s import-table entry for `SetCursorPos`, in the
daemon process only, with a gate evaluated on every call. It forwards to the real function only
while the user is driving the seat through the viewer: control taken, the viewer window visible and
not minimized, and the viewer the foreground window. Otherwise it returns TRUE without moving
anything. Pointer moves inside a focused, controlled viewer still work, as in any Remote Desktop
client.

- Regular and delay-load import tables are both walked. A slot matches by imported name or by
  resolved address, and the listed DLL name is ignored, so API-set redirection or a future move to
  delay loading does not hide it. An unresolved delay-load slot is patched before first use, so the
  loader never gets to overwrite it.
- The module is pinned so recorded slots stay valid. Installing again leaves an intact patch alone
  and re-patches a slot something else rewrote. It runs when the control's window is created and
  again immediately before `Connect`.
- If no import is found, the daemon logs an error and `status.pointerGuard.installed` is false.
  The seat still starts: a missing guard must not take the seat down, but it must not be silent.
- `status.pointerGuard` also reports `suppressed`, `forwarded` and the viewer's rectangle.

Rejected alternatives. Hosting the control on a separate non-input desktop (`CreateDesktop` plus
`SetThreadDesktop`) would also contain it, but the viewer window and tray icon share that UI thread,
and showing the viewer on demand would mean re-creating the control and reconnecting the session. An
extended property that disables pointer-position updates would be the clean fix; none is known.

## Regression checks

- `selftest --quick`, "viewer pointer guard": creates the hidden view-only viewer the daemon
  creates, requires every `SetCursorPos` import of the loaded `mstscax.dll` to point at the gate,
  then calls through those slots the way the module does and requires the pointer to stay put,
  view-only and with control taken (hidden, so still not allowed). It aims one pixel away, so even a
  broken gate cannot throw the pointer across the screen. It also enters the gate from a raw native
  thread, as `mstscax.dll` does, in the compressed single-file build. The policy's truth table is
  checked too.
- `scripts/test-pointer-isolation.ps1`, opt-in, live seat: waits until the real pointer is inside
  the viewer's rectangle (or places it with `-PlacePointer`), runs the probe and watcher, and passes
  only if no jump landed in the rectangle within 250 ms of a seat move, `forwarded` did not grow and
  `suppressed` did. Nothing suppressed means inconclusive, not passed. `-IncludeVisible` and
  `-IncludeControl` cover the shown viewer and the forwarding path. Its leak filter was run over the
  three recordings above under Windows PowerShell 5.1 and classified 1, 0 and 5 leaks; the four
  fast hand movements the watcher recorded on the other monitor were not counted.

## Still to do

Restart the daemon from a build with the guard, once whoever has programs open in the seat agrees,
then run `scripts\test-pointer-isolation.ps1 -PlacePointer -IncludeVisible`. `anode quit` signs the
seat out and closes its programs. The [September 14 record](2026-09-14-hidden-viewer-capture-fails.md)
notes a daemon restart that kept the child session and its applications at the cost of one
user-approved Windows sign-in; whether that is repeatable here has not been tested.
