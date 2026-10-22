# Keep working while Windows-MCP clicks

[Windows-MCP](https://github.com/CursorTouch/Windows-MCP) (CursorTouch, MIT) drives the Windows desktop it runs on.
Since 0.8.8, its control gate pauses the agent whenever you touch your own mouse or keyboard: `ControlStatus`
reports `user`, and its click and type calls fail with `USER_CONTROL`. Without the gate, you and the agent fight
over one pointer.

Anode gives you a way out without changing Windows-MCP. Start Windows-MCP **inside Anode's seat**: a second Windows
session with its own screen, pointer and keyboard focus. Windows-MCP then clicks and types there, its gate sees
only the seat's input, not yours, and your own desktop stays yours.

## What we measured

On 2026-10-22 we ran Anode 0.11.3 with Windows-MCP 0.8.8 (PyPI, Python 3.14.7, telemetry off), over streamable
HTTP on 127.0.0.1. Two clients drove it: a script, and Claude Code 2.1.302 restricted to the Windows-MCP tools. A
sampler read the parent desktop every 50 ms through both client runs.

| Check | Result |
|---|---|
| Windows-MCP's process | in the seat's session, not yours |
| Tool calls (ControlStatus, App, Snapshot, Type, Screenshot) | 7/7 (script) and 6/6 (Claude Code) succeeded |
| `USER_CONTROL` or `TAKEOVER_PENDING` in any result or the server log | never |
| `ControlStatus` | `ready` before, `ai` after: the agent kept control |
| Your pointer, sampled 4,762 times over 240 s | never moved |
| Your foreground window | never changed |
| Text typed by `Type` | landed in a window in the seat |
| Call times | Type 1.4 s, Screenshot 0.4 s, Snapshot 16–26 s |

**What this does and doesn't show:**
- **Why the gate stays quiet:** it watches for physical input through low-level hooks and raw input, and ignores
  its own injected input. Those hooks see only their own session, and nobody types in the seat. If you press
  **Take control** in Anode's viewer, your input does reach the seat, and the gate should then pause the agent as it
  would on your own desktop (not tested).
- **Nobody used the PC during the run.** The sampler separates your own moves from anything else. We haven't yet
  repeated the run with someone working at the same time.
- **Where your pointer was:** it was away from the hidden viewer's rectangle. If it rests there, Anode's pointer
  guard stops seat programs from moving it; see [architecture](ARCHITECTURE.md#design-decisions-worth-defending).
  `anode status` counts these moves on its `your pointer` line.

## Set it up

You need Anode [installed and set up](INSTALL.md) (Windows 10/11 Pro, Enterprise or Education) and
[uv](https://docs.astral.sh/uv/). Windows-MCP 0.8.8 needs Python 3.14 or later.

**1. Install Windows-MCP in a folder of its own.**

```powershell
uv venv --python 3.14 C:\tools\windows-mcp
uv pip install --python C:\tools\windows-mcp\Scripts\python.exe windows-mcp==0.8.8
```

If uv picks a free-threaded build (`3.14t`), pywin32 won't install. Name a regular build instead, for example
`--python cpython-3.14.7-windows-x86_64-none`.

**2. Save a start script** as `C:\tools\windows-mcp\start-windows-mcp.cmd`. It listens on 127.0.0.1 only, turns off
Windows-MCP's anonymous telemetry, enables the six tools we measured, and writes a log next to it.

```bat
@echo off
set ANONYMIZED_TELEMETRY=false
"%~dp0Scripts\windows-mcp.exe" serve --transport streamable-http --host 127.0.0.1 --port 8765 --tools ControlStatus,App,Type,Click,Screenshot,Snapshot >> "%~dp0server.log" 2>&1
```

Any program on your PC can reach that port, and Windows-MCP's other tools include a PowerShell runner. So add only
the tools you need. Windows-MCP's `--auth-key` adds a bearer-token check; your client then sends the same token.

**3. Start it in the seat.** `anode run` starts a program inside the seat. Like every desktop command, it needs the
desktop lease, which you can release straight after.

If you sign in to Windows with a PIN or Windows Hello, a new seat may stop at sign-in, and `anode lease acquire`
then fails. Choose **Sign in…** from the Anode tray icon, for each new seat and again after a **Reconnect**. An agent
can't do this step for you; see [sign-in troubleshooting](TROUBLESHOOTING.md#it-asks-for-a-password-every-time).

```powershell
$env:ANODE_AGENT_ID = 'windows-mcp-start'
$lease = anode lease acquire | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
anode run C:\tools\windows-mcp\start-windows-mcp.cmd | Out-Host
anode lease release | Out-Host
```

Give it up to about 15 seconds to start listening. Then check that the server runs in the seat. Its session must be
the seat's (`anode status` prints it as `seat session`), not yours:

```powershell
Get-NetTCPConnection -LocalPort 8765 -State Listen |
  ForEach-Object { Get-Process -Id $_.OwningProcess } | Select-Object Id, ProcessName, SessionId
```

**4. Point your MCP client at it.** For Claude Code, add this to the project's `.mcp.json`, or run
`claude mcp add --transport http windows-mcp http://127.0.0.1:8765/mcp`:

```json
{
  "mcpServers": {
    "windows-mcp": { "type": "http", "url": "http://127.0.0.1:8765/mcp" }
  }
}
```

Other clients that speak streamable HTTP use the same URL.

**5. Stop it** when you're done. End the process that holds the port; this works across sessions:

```powershell
Get-NetTCPConnection -LocalPort 8765 -State Listen | ForEach-Object { Stop-Process -Id $_.OwningProcess }
```

## Things to know

- **The seat is a desktop of your own account, not an empty sandbox.** Programs that start when you sign in, such
  as chat apps, also open in the seat. Windows-MCP's Screenshot and Snapshot see the whole seat, so minimize or close
  anything you don't want captured. To look at the seat yourself, run `anode show`. Anode shares your files, account
  and network ([security](SECURITY.md#what-the-seat-is-not-isolated-from)).
- **Windows-MCP doesn't take Anode's lease.** If other agents also use Anode's own tools, they and Windows-MCP can act
  in the seat at the same time. Don't use both at once.
- **Don't run `windows-mcp install`.** Its sign-in task would also start Windows-MCP in your own session, where it
  competes for the port and your pointer.
- **Careful with Notepad for test typing.** Windows 11 Notepad keeps its saved tabs per user, not per session, so a
  Notepad started in the seat may open the tabs you left unsaved in your own session. We typed into a throwaway
  window instead.
- **Snapshot took 16–26 s in our run**, including retries on a window that was closing. We didn't compare it
  outside the seat. Screenshot took 0.4 s.

When you'd rather not run two tools, Anode's own MCP server does the same jobs (windows, accessible controls,
screenshots, clicks and typing) in the seat, with the lease built in. See [when to use which](COMPARISON.md).
