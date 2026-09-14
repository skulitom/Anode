# GameInput helper holds foreground and blocks seat input

Status: diagnosed; one-time child-helper repair verified. Recurrence depends on Windows.

In an active child session, capture and UIA actions worked but `SetForegroundWindow` failed.
`SendInput` reported accepted events without changing the browser field or cursor position.
Read-only diagnostics inside the seat identified an invisible `GameInputServiceWindow`, owned
by a SYSTEM GameInputSvc helper in that same session, as the persistent foreground window.
The thread and input desktops were both Default. Attaching to its input thread failed with
access denied. This was independent of the earlier RDP capture suppression problem.

The user approved an administrator prompt. `scripts/repair-seat-input.ps1` verified the current
parent's child through `WTSGetChildSessionId`, checked the specified helper's session, name and
parent service, and stopped only that helper. GameInputSvc and GameInputRedistService remained
running. The main Steam client, boxed Steam client and Liftoff stayed alive.

Afterward, an owned Chrome fixture accepted Anode mouse clicks and keyboard text. The test
asserted the resulting DOM value and captured it through the hidden seat. Window focus needed
an additional worker fix: create its message queue, temporarily attach to the verified seat
foreground/target queues, and wait briefly for asynchronous foreground activation.

Anode now reports this known blocker through capabilities and rejects synthetic input while
that helper owns foreground, rather than returning an ineffective success. The repair is
explicit and one-time; it does not disable services or elevate the seat host. If Windows
recreates the helper and the fault recurs, recheck its identity before another repair.

Independent confirmation of this exact foreground-helper symptom is recorded by the author of
[pytest-uia](https://pypi.org/project/pytest-uia/0.7.1/). Windows documents the constraints on
[thread input attachment](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput).
