# The scheduler-launched daemon writes no log, and a refused connection is reported as a timeout

**Found by:** Claude (Haltere session), 2026-09-12, Windows 11 Pro 25H2 build 26200, Anode `dist\anode.exe` built 21:04.

## 1. No `[daemon]` lines when the daemon is started by `anode start` (Task Scheduler)

After `anode start`, `%LOCALAPPDATA%\Anode\anode.log` only contained the `[cli] launched into session 1: ...anode.exe up ...`
line. The daemon answered on `\\.\pipe\anode-control` (so it was running: `anode status` worked) but none of its
`Log.Info` calls reached the file: not `control pipe listening on ...`, not the `[connecting]` /
`[stopped]` state lines.

When the same executable was started as a child of a shell (`anode up`), the `[daemon]` lines appeared
immediately, including the actual failure (`The Remote Desktop host refused the loopback connection`).

Likely cause: `Env.StateDirectory` is built from `Environment.GetFolderPath(LocalApplicationData)`, and the
process started through `IRegisteredTask.RunEx` does not get the same environment (or the write fails
and `Log.Write` swallows the exception by design). Either way the detached daemon is blind, which is the
mode every MCP client uses.

Suggestions: resolve the state directory once from the launching CLI and pass it to the daemon as an
argument (`up --state-dir ...`), or fall back to `%USERPROFILE%\AppData\Local\Anode` explicitly; and have
`Log.Write` record its own first failure to the console/stderr so it is not silent.

## 2. `seat.start` waits the full 120 s when the RDP host refuses the connection

`OnViewerDisconnected` sets the state to `stopped` ("The seat is gone. ...refused the loopback connection...")
rather than `error`, and `_lastError` is only stored for `error`/`logon-error`. `StartSeatAsync` therefore
keeps polling until its deadline and returns `The seat did not become ready in time`, and `anode status`
shows `lastError: null`. The user sees a timeout instead of the reason and the fix.

Suggestion: when the disconnect happens during bring-up (state `connecting`/`signing-in`), transition to
`error` with the explanation so `seat.start` fails fast with it, or keep `stopped` but store the
explanation in `_lastError` and check it in the `StartSeatAsync` loop.

## 3. Minor: `anode kill` with no seat makes the daemon exit

`anode kill` printed `ok` while no seat existed; afterwards `\\.\pipe\anode-control` was gone and
`anode up` had to start a new daemon. Expected from the README: `kill` stops the seat, `quit` exits Anode.
(Observed once; not investigated further.)
