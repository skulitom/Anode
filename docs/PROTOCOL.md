# Protocol

Two named pipes, one wire format. If you want to drive a seat from something other than the CLI or
an MCP client, this is the whole interface.

## Wire format

Newline-delimited JSON over a named pipe, UTF-8 without a BOM. One request object per line, one
response object per line, in order, on a single connection. No framing headers, no batching.

**Request**

```json
{"op": "input.click", "id": 7, "x": 640, "y": 400, "button": "left"}
```

`op` is required. `id` is optional and echoed back. Everything else is the operation's arguments,
flat on the same object. `timeoutMs` on a request bound for the seat overrides the daemon's default
60 second forwarding timeout.

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

Both live in the machine-global pipe namespace with the default ACL, which grants the creating user.
That is what lets two sessions of the same user talk without any extra rights, and what stops another
user on the machine from driving your seat.

## Operations the daemon owns

| `op` | Arguments | Result |
| --- | --- | --- |
| `ping` | | `{daemon, state}` |
| `status` | | see below |
| `doctor` | | `{checks: [{name, state, detail, fix}]}` |
| `seat.start` | | `{session}` when ready |
| `seat.stop` | `reason` | `{stopped, session}` |
| `seat.show` | | |
| `seat.hide` | | |
| `seat.control` | `viewOnly` | `{viewOnly}` |
| `quit` | | stops the seat, then exits Anode |

`status` result:

```json
{
  "state": "ready",
  "session": 3,
  "agentReady": true,
  "viewerConnection": 1,
  "viewerVisible": true,
  "viewOnly": true,
  "parentSession": 1,
  "uptimeSeconds": 184.2,
  "lastError": null,
  "logPath": "C:\\Users\\you\\AppData\\Local\\Anode\\anode.log",
  "seat":  { "session": 3, "pid": 9120, "user": "you", "screen": {"width":1280,"height":720}, "cursor": {"x":640,"y":360} },
  "steam": { "steamExe": "D:\\STEAM\\steam.exe", "runningSessions": [3], "summary": "Steam is running inside the seat..." }
}
```

`state` moves through `starting` → `connecting` → `signing-in` → `starting-agent` → `ready`, and can
land on `detached` (seat alive, viewer disconnected), `stopping`, `stopped`, `error` or `logon-error`.

## Operations the seat host owns

Anything the daemon does not recognise is forwarded to the seat host unchanged. A new seat capability
therefore needs no daemon change.

### State

| `op` | Arguments | Result |
| --- | --- | --- |
| `ping` | | `{session, pid, user, uptimeSeconds, screen:{width,height}, cursor:{x,y}}` |
| `screenshot` | `maxWidth`, `format` (`png`\|`jpeg`), `quality` | `{data (base64), mimeType, width, height, sourceWidth, sourceHeight, bytes}` |

`width`/`height` are the returned image; `sourceWidth`/`sourceHeight` are the seat's real screen.
**Click coordinates always use the source size**, so downscaling a screenshot costs tokens, not
accuracy.

### Input

All coordinates are pixels on the seat's screen, origin top left. Keys are sent as scan codes by
default (`scanCode: false` to send virtual-key events instead), which is what makes games see them.

| `op` | Arguments |
| --- | --- |
| `input.move` | `x`,`y` (absolute) or `dx`,`dy` (relative, for games that read raw motion) |
| `input.click` | `button`, `x`, `y`, `count`, `holdMs` |
| `input.down` / `input.up` | `button`, `x`, `y` |
| `input.drag` | `fromX`, `fromY`, `toX`, `toY`, `button`, `steps`, `stepMs` |
| `input.scroll` | `amount` (negative is down), `horizontal` |
| `input.key` | `keys` (`"ctrl+shift+esc"`), `holdMs`, `scanCode` |
| `input.keydown` / `input.keyup` | `key`, `scanCode` |
| `input.text` | `text`, `perCharMs` |

Buttons: `left`, `right`, `middle`, `x1`, `x2`. Key names: letters, digits, `f1`–`f24`, `num0`–`num9`,
`enter`, `esc`, `space`, `tab`, `backspace`, `del`, `ins`, `home`, `end`, `pgup`, `pgdn`, arrows,
`ctrl`, `shift`, `alt`, `win`, `apps`, punctuation by name or by symbol, and `vk<hex>` for anything else.

### Programs

| `op` | Arguments | Result |
| --- | --- | --- |
| `run` | `path`, `args` (array), `cwd` | `{pid, path, session}` |
| `steam.status` | | `{steamExe, runningSessions, summary}` |
| `steam.launch` | `appId`, `args`, `force`, `startClient`, `clientWarmupMs` | `{appId, session, note}` |
| `ps.list` | `windowedOnly` | `{session, processes:[{pid,name,title,started,memoryMb}]}` |
| `ps.kill` | `pid` or `name` | `{killed}` |

`steam.launch` fails with an explanation when Steam is already running outside the seat, because the
game would open on your screen. `force: true` overrides. `ps.kill` refuses any pid outside the seat's
session.

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
maps to exactly one `op`, so there is no second implementation to keep in step.

| Tool | `op` | Tool | `op` |
| --- | --- | --- | --- |
| `seat_status` | `status` | `seat_click` | `input.click` |
| `seat_start` | `seat.start` | `seat_move` | `input.move` |
| `seat_stop` | `seat.stop` | `seat_drag` | `input.drag` |
| `seat_show` / `seat_hide` | `seat.show` / `seat.hide` | `seat_scroll` | `input.scroll` |
| `seat_screenshot` | `screenshot` | `seat_key` | `input.key` |
| `seat_run` | `run` | `seat_type` | `input.text` |
| `steam_status` | `steam.status` | `gamepad_attach` | `gamepad.attach` |
| `steam_launch` | `steam.launch` | `gamepad_detach` | `gamepad.detach` |
| `seat_processes` | `ps.list` | `gamepad_set` | `gamepad.set` |
| `seat_kill_process` | `ps.kill` | `gamepad_tap` | `gamepad.tap` |
| | | `gamepad_reset` | `gamepad.reset` |

`seat_screenshot` is the one tool whose response is reshaped: it returns an MCP `image` content block
plus a line of text giving the capture size, so a model can look at the seat directly.

Tools marked as starting the daemon (`seat_status`, `seat_start`, and everything that acts on a live
seat) will launch `anode up` if it is not running and wait for the seat to become ready. `seat_stop`
and the other teardown tools never start anything.
