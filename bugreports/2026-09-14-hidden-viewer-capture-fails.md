# Hidden viewer can leave the seat ready but unable to capture

Status: hidden capture reproduced with the live September 13 daemon. New control
inspection/actions validated in the child host; rendering mitigation awaits daemon restart.

The daemon remains ready, its host runs in verified child session 5, and qwinsta
reports the child active. With the parent viewer minimized, and later after using
`anode hide`, `anode shot` fails with `Win32Exception: The handle is invalid`.
Showing/restoring the viewer recovered capture temporarily. An immediate capture
after hiding also succeeded, but later hidden captures failed. Hide alone is
therefore not a reliable workaround. The host log records the screenshot handler
exception; this does not mean the process or child session has stopped.

The capture implementation uses Graphics.CopyFromScreen in the child host.
Investigate RDP display suppression and desktop availability while hidden or
minimized. Do not infer that the current session is usable for visual input merely
from its ready state. RemoteDesktop_SuppressWhenMinimized was absent from the
current user's Terminal Server Client registry key during this investigation;
no registry setting was changed and its effect on hidden ActiveX controls remains
to be validated.

The foreground Computer Use fallback interfered with the user's desktop (blue
overlay and separate cursor). The user explicitly rejected this workflow.
Use Anode's own capture and input tools only for seat operations. Do not silently
show/activate its parent viewer or invoke foreground Computer Use to recover.
Pause visual input when capture fails and diagnose through code/logs instead.
The viewer was hidden again after the user clarified this requirement.

Keep the working seat and both Steam clients alive while investigating. Liftoff
PID 4008 launched in Sandboxie box AnodeLiftoff in session 5, with RTX 4090 rendering
reported by the game. Main Steam PID 63080 stayed in session 3. Controller input
has not been tested and remains machine-wide.

## September 14 implementation and validation

Anode 0.2 adds window enumeration, bounded UI Automation inspection/actions, and
searchable HTML reports. Live tests read controls and performed text replacement,
button invocation, checkbox toggling, list selection, two slider provider types,
read-only action filtering and window movement while the viewer stayed hidden. All
13 checks in `scripts/test-desktop.ps1` passed. A failed
screenshot is reported explicitly alongside the usable control tree. No foreground
capture or viewer fallback is used, and password values are omitted.

The updated daemon sets the per-user RDP rendering preference before ActiveX
creation, preserving the previous value for restoration. Minimizing the viewer now
hides it to the tray. These changes are not yet proven to fix this machine's hidden
capture failure: the original daemon remains connected to preserve the running game
and both Steam clients. Replacing just the seat host is insufficient to activate a
setting consumed by the parent RDP client. Verify capture after an arranged daemon
restart; do not treat `backgroundRenderingConfigured` as a successful capture test.
