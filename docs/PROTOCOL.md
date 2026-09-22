# Protocol

Two named pipes, one wire format. If you want to drive a seat from something other than the CLI or
an MCP client, this is the whole interface.

## Wire format

Newline-delimited JSON over a named pipe, UTF-8 without a BOM. One request object per line, one
response object per line, in order, on a single connection. No framing headers, no batching.
Property names must be unique within each object, including nested arguments, and strings must
contain well-formed Unicode. Malformed or ambiguous JSON is rejected before dispatch; the connection
remains usable for the next request.

**Request**

```json
{"op": "input.click", "id": 7, "agentId": "agent-1", "leaseToken": "l_RETURNED_TOKEN", "x": 640, "y": 400, "button": "left"}
```

`op` is required. `id` is optional and echoed back. Everything else is the operation's arguments,
flat on the same object. `timeoutMs` on a request bound for the seat overrides the daemon's default
60 second forwarding timeout. Client deadlines include queueing, writing and reading. A timeout
after sending closes that connection, because a late reply must not become the next command's
result. Reconnect for subsequent requests; a timed-out command may already have executed.

Desktop operations additionally require an `agentId` and live `leaseToken`, acquired through
`lease` with `action: "acquire"`, and may carry an `agentName` for status and the viewer. `lease`
also supports `status`, `renew` and `release`, with `ttlSeconds` (10-600, default 120) on
acquire/renew, `waitSeconds` (0-300) and `startSeat` on acquire, and `cancelJobs` on release. A
desktop operation extends its lease to a full `ttlSeconds` from when it starts; while other agents
wait, its response carries `waitingAgents`. Owned job reads require `agentId` but no lease. See
[the full ownership protocol](MULTI-AGENT.md#protocol-and-upgrades).

**Response**

```json
{"ok": true, "result": {"x": 640, "y": 400}, "id": 7}
{"ok": false, "error": "No virtual controller in slot 1. Attach one first (gamepad_attach).", "id": 8}
```

`ok` is always present. `result` is present on success and may be absent when there is nothing to
say. `error` is a sentence meant to be shown to a person.

## The pipes

| Pipe | Server | Clients |
| --- | --- | --- |
| `\\.\pipe\anode-control` | the daemon, in your session | `anode <cmd>`, `anode mcp`, anything you write |
| `\\.\pipe\anode-seat` | the seat host, inside the child session | the daemon only |

These are the main channel's names. Another channel, such as a Debug build's `dev`, appends its
name: `anode-control-dev` and `anode-seat-dev`. Both live in the machine-global pipe namespace.
Servers and clients use .NET's
`PipeOptions.CurrentUserOnly`, restricting connections to the same Windows identity and elevation
level. Run the daemon and clients from ordinary, unelevated terminals; only setup needs elevation.
See [Microsoft's pipe option documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions).

## Operations the daemon owns

| `op` | Arguments | Result |
| --- | --- | --- |
| `ping` | | `{daemon, state}` |
| `status` | | see below |
| `seat.identity` | | `{session, parentSession}` from Windows; used by the seat host to verify its session before serving input |
| `doctor` | | `{checks: [{name, state, detail, fix}]}` |
| `seat.start` | | `{session}` when ready |
| `seat.stop` (alias `kill`) | `reason` | `{stopped, session}` |
| `seat.show` | | |
| `seat.hide` | | |
| `seat.control` | `viewOnly` | `{viewOnly}` |
| `lease` | `action`, `ttlSeconds`, `cancelJobs`, `agentId`, `leaseToken` | `{agentId, ownerAgentId, expiresInMs, operationRunning, leaseToken, summary}`; `acquire` starts the seat if needed; the request then goes to the seat host, which owns the lease, on an independent connection. See [multiple agents](MULTI-AGENT.md). |
| `quit` | | stops the seat, then exits Anode |

`status` result:

```json
{
  "state": "ready",
  "channel": "main",
  "session": 3,
  "agentReady": true,
  "viewerConnection": 1,
  "viewerVisible": true,
  "viewOnly": true,
  "pointerGuard": { "installed": true, "patchedImports": 1, "suppressed": 61, "forwarded": 0,
                    "viewer": {"x":400,"y":167,"width":1280,"height":720} },
  "parentSession": 1,
  "uptimeSeconds": 184.2,
  "lastError": null,
  "logPath": "C:\\Users\\you\\AppData\\Local\\Anode\\anode.log",
  "logError": null,
  "seat":  { "session": 3, "pid": 9120, "user": "you", "screen": {"width":1280,"height":720}, "cursor": {"x":640,"y":360} },
  "steam": { "steamExe": "D:\\STEAM\\steam.exe", "seatSession": 3, "runningSessions": [3],
             "runningOutsideSeat": false, "summary": "Steam is running inside the seat..." }
}
```

`state` moves through `starting` → `connecting` → `signing-in` → `starting-agent` → `ready`, and can
land on `detached` (seat alive, viewer disconnected), `stopping`, `stopped`, `error` or `logon-error`.
A viewer disconnect during startup becomes `error`, preserving the disconnect code and explanation
in `lastError`. Startup waits return that failure immediately. `logError` reports the most recent
log-write failure and clears after a successful write; `logPath` is the actual resolved destination.
`channel` is `main` for the installed Anode, or the development channel, such as `dev`, whose pipes
this daemon serves.

`pointerGuard` describes the gate on the Remote Desktop control's `SetCursorPos` import. `suppressed`
counts seat pointer moves kept off the user's desktop, `forwarded` those applied because the user had
taken control in a visible, focused viewer, and `viewer` is the control's rectangle on the user's
desktop, which exists even while the viewer is hidden. `installed: false` means the guard could not
be verified; new connections and reconnects are refused. See
[Troubleshooting](TROUBLESHOOTING.md#my-real-pointer-jumps-while-something-runs-in-the-seat).

## Operations the seat host owns

Anything the daemon does not recognize is forwarded to the seat host unchanged. A new seat capability
therefore needs no daemon change.

### State

| `op` | Arguments | Result |
| --- | --- | --- |
| `ping` | | `{session, pid, user, uptimeSeconds, screen:{width,height}, cursor:{x,y}}` |
| `screenshot` | `maxWidth`, `format` (`png`\|`jpeg`), `quality` | `{data (base64), mimeType, width, height, sourceWidth, sourceHeight, bytes}` |

`width`/`height` are the returned image; `sourceWidth`/`sourceHeight` are the seat's real screen.
**Click coordinates always use the source size**, so downscaling a screenshot costs tokens, not
accuracy.

### Desktop inspection and actions

| `op` | Arguments | Result |
| --- | --- | --- |
| `desktop.windows` | `query`, `pid` | `{windows: [{windowId, pid, process, title, bounds, foreground, minimized, maximized}], summary}` |
| `desktop.observe` | `windowId`, `maxElements`, `maxDepth`, `maxTextChars`, `includeOffscreen`, `includeScreenshot`, `maxWidth` | `{windowId, snapshotId, expiresInSeconds, window, elements, observedAt, truncated, warnings, screenshot?, screenshotError?, summary}` |
| `desktop.window` | `windowId`, `action`, optional move geometry `x`, `y`, `width`, `height` | `{requested, note, summary}` |
| `desktop.element` | `snapshotId`, `elementId`, `action`, optional `value`, `number`, `direction`, `amount` | `{performed, note, summary}` |
| `desktop.capabilities` | `probeCapture` (default true) | `{version, session, workingDirectory, screen, capture, input, supported, limitations, summary}` |
| `desktop.wait` | `windowId`, selectors `automationId`, `name`, `role`, `textContains`, `state`, `waitMs` | `{matched, waitElapsedMs, ...observation, summary}` |

Window IDs last ten minutes. Snapshots last 90 seconds and are consumed on an
attempted element action, including a timeout. Inputs and window actions invalidate
observations. Accessibility providers execute in a verified child-session worker
with a ten-second deadline. Password values are omitted. Screenshot failure leaves
the accessible tree available and adds `screenshotError`; it does not trigger a
foreground fallback. Limits, states and actions are documented in
[Desktop tools](DESKTOP-TOOLS.md).

Window action `raise` brings a window forward without requesting keyboard focus. Focus is verified
against the actual foreground window. A known foreground GameInput helper is reported as an input
blocker; synthetic input fails explicitly instead of claiming it reached an application.

### Execution jobs

| `op` | Arguments | Result |
| --- | --- | --- |
| `exec.start` | `path`, literal `args`, absolute `cwd`, string-map `env`, `executionTimeoutMs`, `waitMs` | `{jobId, state, finished, exitCode, stdout, stderr, cursor, outputTruncated, hasMoreOutput, session, workerPid, error, elapsedMs, summary}` |
| `exec.read` | `jobId`, `action` (`read` or `cancel`), `after`, `waitMs`, `maxChars` | same job result |
| `exec.read` | `action: "list"` only | `{jobs: [{jobId, state, finished, exitCode, startedUtc}], summary}` |

Commands use a verified child-session worker assigned to a Windows job before receiving its request.
Already cancelled requests do not start or cancel jobs. Client cancellation does not replay or cancel
an already launched job. Output is UTF-8; `maxChars` counts Unicode code points, preserving complete
characters at page boundaries. Reuse returned cursors exactly. Jobs end on their own deadline,
explicit cancellation, completion or host exit; existing applications remain outside their process tree.
See [DEVELOPMENT-TESTING.md](DEVELOPMENT-TESTING.md) for bounds, exit semantics and cleanup.

### Input

All coordinates are pixels on the seat's screen, origin top left. Keys are sent as scan codes, which
is what makes games see them; only `input.keydown`/`input.keyup` accept `scanCode: false` to send
virtual-key events instead. An operation that has an MCP tool is checked against that tool's input
schema, so its row lists every argument it accepts.

| `op` | Arguments |
| --- | --- |
| `input.move` | `x`,`y` (absolute) or `dx`,`dy` (relative, for games that read raw motion) |
| `input.click` | `button`, `x`, `y`, `count` |
| `input.down` / `input.up` | `button`, `x`, `y` |
| `input.drag` | `fromX`, `fromY`, `toX`, `toY`, `button` |
| `input.scroll` | `amount` (negative is down), `horizontal` |
| `input.key` | `keys` (`"ctrl+shift+esc"`), `holdMs` |
| `input.keydown` / `input.keyup` | `key`, `scanCode` |
| `input.text` | `text`, `perCharMs` |

Buttons: `left`, `right`, `middle`, `x1`, `x2`. Key names: letters, digits, `f1`–`f24`, `num0`–`num9`,
`enter`, `esc`, `space`, `tab`, `backspace`, `del`, `ins`, `home`, `end`, `pgup`, `pgdn`, arrows,
`ctrl`, `shift`, `alt`, `win`, `apps`, punctuation by name or by symbol, and `vk<hex>` for anything else.

### Programs

| `op` | Arguments | Result |
| --- | --- | --- |
| `run` | `path`, `args` (array), `cwd` | `{pid, path, session}` |
| `steam.status` | | `{steamExe, seatSession, runningSessions, runningOutsideSeat, summary}` |
| `steam.launch` | `appId`, `args`, `force` | `{appId, session, note}` |
| `ps.list` | `windowedOnly` | `{session, processes:[{pid,name,title,started,memoryMb}]}` |
| `ps.kill` | `pid` or `name` | `{killed}` |

`steam.launch` fails with an explanation when Steam is already running outside the seat: another
launch can disrupt that client or open the game on its screen. `force: true` overrides this protection;
it does not create an independent client. `ps.kill` refuses any pid outside the seat's
session.

`run` also refuses direct `steam.exe`/`steam` commands and `steam://` URLs when Steam is running
outside the seat. Starting another client can disrupt the existing one. This check does not inspect
shortcuts or wrapper scripts. Other applications may also reuse an existing instance in another
session; a `run` response confirms where the launch originated, not where every resulting window
will appear.

### Gamepad

| `op` | Arguments |
| --- | --- |
| `gamepad.attach` / `gamepad.detach` / `gamepad.reset` | `slot` (0-3) |
| `gamepad.set` | `slot`, `buttons` `{name: bool}`, `axes` `{lx,ly,rx,ry: -1..1}`, `triggers` `{lt,rt: 0..1}` |
| `gamepad.tap` | `slot`, `button`, `ms` |
| `gamepad.state` | |

`gamepad.set` is **sticky**: only the fields you pass change, and the whole report is resubmitted. So
holding a stick while tapping a button is two calls, not one combined call.

Buttons: `a b x y lb rb back start guide ls rs up down left right`, plus `lt` and `rt` for
`gamepad.tap`. Axes follow the Xbox convention, `ly` and `ry` positive upwards.

### Lifecycle

| `op` | Effect |
| --- | --- |
| `shutdown` | The seat host exits. The seat itself keeps running. |

## Talking to it yourself

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'anode-control', 'InOut')
$pipe.Connect(2000)
$writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($pipe)
$writer.WriteLine('{"op":"status","id":1}')
$reader.ReadLine() | ConvertFrom-Json | ConvertTo-Json -Depth 6
```

## MCP mapping

`anode mcp` is a thin translation of the same operations into Model Context Protocol tools. Each tool
except `anode_guide` maps to exactly one `op`, so there is no second implementation to keep in step.

| Tool | `op` | Tool | `op` |
| --- | --- | --- | --- |
| `anode_guide` | none; answered locally | `seat_click` | `input.click` |
| `seat_status` | `status` | `seat_move` | `input.move` |
| `seat_lease` | `lease` | `seat_drag` | `input.drag` |
| `seat_start` | `seat.start` | `seat_scroll` | `input.scroll` |
| `seat_stop` | `seat.stop` | `seat_key` | `input.key` |
| `seat_show` / `seat_hide` | `seat.show` / `seat.hide` | `seat_type` | `input.text` |
| `seat_capabilities` | `desktop.capabilities` | `gamepad_attach` | `gamepad.attach` |
| `seat_screenshot` | `screenshot` | `gamepad_detach` | `gamepad.detach` |
| `seat_run` | `run` | `gamepad_set` | `gamepad.set` |
| `steam_status` | `steam.status` | `gamepad_tap` | `gamepad.tap` |
| `steam_launch` | `steam.launch` | `gamepad_reset` | `gamepad.reset` |
| `seat_processes` | `ps.list` | `seat_windows` | `desktop.windows` |
| `seat_kill_process` | `ps.kill` | `seat_observe` | `desktop.observe` |
| `seat_exec` | `exec.start` | `seat_window` | `desktop.window` |
| `seat_job` | `exec.read` | `seat_element` | `desktop.element` |
| | | `seat_wait` | `desktop.wait` |

There are 32 tools. MCP assigns an agent ID (or uses `ANODE_AGENT_ID` from its environment),
remembers the token returned by `seat_lease` acquire or renew, and supplies both on desktop calls.
`anode_guide` returns the embedded operating guide without a daemon, setup or waiting behind long
tool calls.

Only `seat_start` and `seat_lease` with `action: "acquire"` start anything: with no daemon running
they launch a hidden one through the shared launcher, and they wait for the seat to be ready.
`seat_status` never starts a daemon. With none running it returns a successful result containing
`state: stopped`, `daemonRunning: false`, `agentId`, `ownerAgentId: null` and a `summary` that says
to acquire a lease; with one running it returns that daemon's status. `seat_capabilities`,
`seat_processes` and `steam_status` likewise report a stopped seat instead of starting one, and
`seat_show` and `seat_hide` return an error. A lease-requiring tool that finds no daemon forgets
its token and returns an error asking for a new acquisition; it never relaunches a daemon a person
quit. The same holds when a person stops the seat but Anode keeps running (the Stop button, the
hotkey, `anode kill` or another agent's `seat_stop`): the daemon answers `errorCode: "seat_stopped"`,
diagnostics report `state: stopped` with `daemonRunning: true`, and lease-requiring tools forget
their token and ask for a new acquisition. The first call after Anode quits reports the same
instead of a lost connection; it is never resent.

A result with a `summary` returns it as text. When `seat_observe` also returns an image block, the
result carries no `structuredContent`; the text then gives the capture size and each control's
automation ID and bounds, so selectors and coordinates work from text alone. Otherwise
`structuredContent` holds the result without its `summary`. `seat_screenshot` returns an image
block plus capture-size text. Errors from the daemon or seat host return `isError: true` with the
failure object, including any `errorCode`, in `structuredContent`.

Annotations follow each tool's effect. `anode_guide`, `seat_status`, `seat_windows`, `seat_observe`,
`seat_screenshot`, `seat_wait`, `seat_capabilities`, `seat_processes` and `steam_status` are
read-only (`readOnlyHint: true`). `seat_windows`, `seat_observe`, `seat_screenshot` and `seat_wait`
return whatever the seat's apps and web pages show, so they set `openWorldHint: true` to mark that
content as untrusted; the other read-only tools set it false. `seat_start` and `seat_hide` are additive
(`destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false`). Every other tool keeps
conservative hints (`destructiveHint: true`, `idempotentHint: false`, `openWorldHint: true`),
including `seat_show`, which puts the viewer on the user's screen. `annotations.title` repeats the
tool's title. Annotations describe effects; they are not approval overrides, and they never make a
timed-out action safe to replay. Titles and initialization instructions provide task-selection
guidance. See [For agents](FOR-AGENTS.md).

The server also offers two prompts, answered locally like `anode_guide`: `desktop_test` takes no
arguments and returns the lease workflow for the app or task the user names, and `desktop_guide`
returns the full guide. In Claude Code, `/mcp__anode__desktop_test` runs one (its `/` menu lists it
as `/anode:desktop_test`); in VS Code, `/mcp.anode.desktop_test`. The names follow the server name
in the client's configuration. An unknown prompt name returns JSON-RPC error `-32602`.

The stdio server supports MCP versions `2024-11-05`, `2025-03-26`, `2025-06-18` and `2025-11-25`.
An unsupported version negotiates `2025-11-25` rather than echoing a version the server does not know.
Malformed JSON, invalid request envelopes and invalid parameters return JSON-RPC errors; invalid
tool arguments return a tool result with `isError: true`. Notifications never execute tools.

Normal tool calls execute in arrival order. Protocol messages remain responsive during a tool call.
`seat_stop` cancels outstanding tool requests and reaches the daemon through a separate pipe;
new tool calls are rejected while Stop is pending. A cancelled command already delivered to the
daemon may have executed or may still be running until the seat stops. Anode never replays it.

`notifications/cancelled` cancels the matching request without a response. Closing stdin cancels
outstanding requests and exits the MCP server; it does not sign out the seat. These behaviors follow
the MCP [lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle) and
[cancellation](https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/cancellation) specifications.
