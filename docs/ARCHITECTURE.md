# Architecture

## The problem

An agent needs somewhere to click that is not where you are clicking. On Windows, "somewhere" is
usually taken to mean a virtual display, and a virtual display does not solve it. A Windows session
has exactly one input queue, one foreground window, one focus, one cursor and one clipboard, no
matter how many monitors are attached to it. Two actors in one session interleave; they do not
coexist.

The unit of isolation on Windows is the **session**, not the display. Anode uses a child session.

## Child sessions in one paragraph

A child session is a loopback Remote Desktop session tied to the signed-in user's session,
[documented since Windows 8](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions).
It is created by hosting the Remote Desktop ActiveX control, setting the `ConnectToChildSession`
extended property, and connecting to `localhost`. Windows signs it in with the current user's
credentials without prompting, gives it no lock screen and no screen saver, and terminates it when
the parent session terminates. Exactly one may be connected at a time. Because it is a real session,
it has its own everything: desktop, input queue, focus, window list, processes.

## Processes

Anode is one executable in four roles. One binary means the seat host is always the same build as
the daemon that launched it.

| Role | Command | Session | Job |
| --- | --- | --- | --- |
| Daemon | `anode up` | yours | Own the seat's lifecycle, host the viewer, serve the control pipe |
| Seat host | `anode __seat-host` | the seat | Input, capture, launching, gamepad, process control |
| CLI | `anode <cmd>` | yours | Thin client of the control pipe |
| MCP server | `anode mcp` | yours | Thin client of the control pipe, speaking JSON-RPC on stdio |
| Elevated setup | `anode __apply-setup` | yours, elevated | The only code that changes machine state |

```
                          your session                              the seat
   ┌──────────────┐   ┌──────────────────────────────┐   ┌──────────────────────────┐
   │  anode mcp   │──▶│                              │   │  anode __seat-host       │
   ├──────────────┤   │   anode up  (daemon)         │   │                          │
   │  anode CLI   │──▶│                              │   │   SendInput              │
   └──────────────┘   │   ┌────────────────────────┐ │   │   CopyFromScreen         │
    \\.\pipe\          │   │ SeatWindow             │ │   │   Process.Start          │
    anode-control     │   │  ┌──────────────────┐  │ │   │   ViGEm gamepad          │
                      │   │  │ RDP ActiveX      │──┼─┼──▶│   (all session-local)    │
                      │   │  │ ConnectToChild.. │  │ │   │                          │
                      │   │  └──────────────────┘  │ │   └────────────┬─────────────┘
                      │   │  [Stop seat] [Control] │ │                │
                      │   └────────────────────────┘ │   \\.\pipe\anode-seat
                      │              │               │◀───────────────┘
                      └──────────────┼───────────────┘
                                     │ WTSLogoffSession
                                     ▼
                              the whole seat, gone
```

## Bring-up sequence

1. **Preconditions.** `anode up` refuses to start if Remote Desktop is off, child sessions are off,
   or the edition is Home. It prints the exact command that fixes each one.
2. **Viewer connects.** `RdpViewer.ConnectToChildSession` sets `Server = localhost`,
   `ConnectToChildSession = true`, CredSSP on, redirection of clipboard, drives, printers, ports and
   smart cards all **off** by default, then calls `Connect()`.
3. **Windows signs the seat in.** The control raises `OnConnecting`, `OnConnected`, `OnLoginComplete`.
4. **Session id appears.** `WTSGetChildSessionId` starts returning a real id a moment after connect,
   so the daemon polls it for up to 60 seconds.
5. **Seat host is placed.** `SeatLauncher` registers a hidden, temporary Task Scheduler task running
   as the current user with an interactive token, calls `RunEx(null, TASK_RUN_USE_SESSION_ID, id, null)`,
   and deletes the task. This is the documented way for a process in one session to start a process in
   another, and it needs no elevation because it is the same user.
6. **Handshake.** The seat host serves `\\.\pipe\anode-seat`; the daemon connects, retrying for 90
   seconds because a fresh session has to start Explorer first, then sends `ping`.
7. **Ready.** State goes to `ready` and the control pipe starts forwarding.

## Where the isolation actually comes from

Not from a policy or a sandbox. From *which process runs where*.

`SendInput` posts to the input queue of the calling process's session. Screen capture reads the
calling process's desktop. `Process.Start` creates a child in the calling process's session. Every
one of those calls lives in the seat host, and the seat host runs inside the child session. There is
no cross-session injection primitive anywhere in Anode, so there is nothing to get wrong at runtime.

As a belt-and-braces check, `SeatHost.Run` compares its own session id against `WTSGetChildSessionId`
and exits with a diagnostic if they differ. If the Task Scheduler hand-off ever misfired, the worst
case is a process that refuses to start, not one that types onto your screen.

The daemon, in your session, holds no input primitives at all. It can start a seat, forward a
request and kill a seat.

## Stopping

One path, four triggers: the toolbar button, the tray menu, `Ctrl+Alt+Shift+K`, and `anode kill`
(or the `seat_stop` MCP tool).

```
StopSeatAsync
  ├─ tell the seat host to shut down     (1.5 s budget, best effort)
  ├─ WTSLogoffSession(child session)     (force-terminates everything in the seat)
  │    └─ on failure: kill every process with that session id, by hand
  ├─ disconnect the viewer
  └─ report what was stopped
```

The short budget on the first step is deliberate. The reason someone is pressing stop is often that
something in the seat is wedged, and waiting politely on a wedged process defeats the purpose.

Closing the viewer window does **not** stop the seat; it hides to the tray. The seat is a thing you
end on purpose.

## Transport

Newline-delimited JSON over named pipes, request then response, on one connection. Two hops:
CLI or MCP to daemon over `anode-control`, daemon to seat host over `anode-seat`. Named pipes are
machine-global and the default ACL grants the creating user, which is what lets the two sessions of
one user talk without any extra rights.

The daemon handles the handful of operations it owns (`status`, `seat.stop`, `seat.start`,
`seat.show`, `seat.hide`, `seat.control`, `doctor`, `quit`) and **forwards everything else** to the
seat host unchanged. Adding a capability to the seat therefore needs no daemon change, and the CLI
and MCP server never diverge because there is only one implementation. Full operation list in
[PROTOCOL.md](PROTOCOL.md).

## Design decisions worth defending

**One executable, five roles.** The seat host is launched by path from the daemon. If they were
separate binaries, a partial upgrade would pair a new daemon with an old host across a pipe protocol.

**The viewer does not own the seat.** An agent should be able to work whether or not anyone is
watching, and a person should be able to close a window without destroying an agent's work. So the
window hides, and only an explicit stop ends the seat.

**View-only by default.** The seat is isolated from you by construction; the viewer is the one place
that isolation could leak, because it is a window on your desktop that forwards input. It starts
refusing your input (`EnableWindow(false)` on the control, which blocks input while the picture keeps
painting) and you opt in with **Take control**.

**No-activate on first show.** A seat coming up must not steal the keystroke you are in the middle of
typing. The window is created with `WS_EX_NOACTIVATE`, which is cleared once connected so that later
clicks behave normally.

**Redirection off by default.** Clipboard, drives, printers, ports and smart cards are all off. A seat
that cannot read your clipboard is a seat an agent cannot accidentally exfiltrate it from. `--clipboard`
turns sharing on when you want it.

**Late-bound COM, hand-written interfaces.** Only `IMsRdpExtendedSettings` and `IMsTscAxEvents` are
declared by hand; the rest of the control is reached through `IDispatch`. That keeps the build free of
`tlbimp` and a Visual Studio dependency, and lets settings that differ between Windows builds fail
softly instead of failing the connection.
