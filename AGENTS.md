# Working on Anode

Anode is a Windows-only .NET 8 application. One executable implements the CLI,
WinForms/RDP daemon, child-session host, setup and MCP stdio server.

## Build and verify

Run from the repository root in PowerShell:

```powershell
dotnet build Anode.sln --nologo
& .\src\Anode\bin\Debug\net8.0-windows\win-x64\anode.exe selftest --quick | Out-Host
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest
```

The last command publishes and tests `dist\anode.exe`. Build output is ignored by
Git. Use the freshly built executable, since an existing `dist` may be stale.
Pipe GUI-executable output to `Out-Host` so PowerShell waits and displays it;
check `$LASTEXITCODE` for failures. There is no separate `dotnet test` project:
regression checks live in `Cli/SelfTest.cs`, `Cli/TransportChecks.cs` and `Cli/McpChecks.cs`.

`selftest --quick` uses private test pipes and a disposable MCP subprocess. It
does not create a seat or inject input. Full `selftest` also captures the current
desktop, launches a scheduler test process and exercises a machine-wide gamepad;
choose it only when those actions are appropriate to the task.

`doctor` reads prerequisites and exits nonzero when setup is missing. Live seat
tests require Remote Desktop and child sessions. `setup` changes machine settings
through UAC; do not treat a missing setup as a failed build.

## Design constraints

- Preserve the seat host's refusal to run in an unverified or parent session.
  The parent daemon resolves child-session identity; the host verifies it before
  serving input. Keep input and capture inside the seat host.
- Keep CLI and MCP daemon startup on the shared `Core/Launch/DaemonLauncher.cs`
  path so Task Scheduler can detach the daemon from an agent's process lifetime.
- Keep MCP stdout exclusively newline-delimited JSON. Diagnostics go to the log
  or stderr. MCP tool discovery must work without machine setup or a live seat.
- Keep protocol processing independent of long tool calls. Ordinary tools execute
  in order; Stop uses a separate daemon connection and cancels outstanding work.
  Validate arguments before any daemon startup or action. Test MCP scheduling
  with injected handlers and private pipes, without creating a real seat.
- Pipe deadlines include queue time. Never replay a timed-out input command:
  its effects may already have happened. Reconnect before subsequent requests.
- Keep Stop able to cancel startup and avoid waiting on long input operations.
- Screenshot click coordinates use the original screen size, even when the
  returned image is smaller. Gamepads are visible across Windows sessions.

See `docs/ARCHITECTURE.md`, `docs/PROTOCOL.md`, `docs/SECURITY.md` and
`docs/CONNECTING-AGENTS.md` for the runtime model and client setup.

## Live testing without disturbing the user

Use Anode's CLI/MCP tools for seat screenshots, mouse, keyboard and application
launches. The user explicitly wants their foreground desktop left available.
Do not invoke foreground Computer Use, activate the parent viewer, or show it as
a fallback when seat capture fails. Diagnose that failure through code and logs;
stop input that requires a fresh screenshot. Show the viewer only when the user
requests it or agrees to a specific interactive step. Virtual gamepads remain
machine-wide and require separate consideration before input tests.

Use `windows`/`inspect` or `seat_windows`/`seat_observe` for accessible controls.
Control actions consume their snapshot; inspect again instead of replaying an
uncertain action. `scripts/test-desktop.ps1` is an opt-in test using a disposable
fixture in an already ready seat; it does not start the seat or show its viewer.
