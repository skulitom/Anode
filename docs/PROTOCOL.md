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
| `seat.display` | none, or `width`, `height`, `scale` and `method` (`live` or `reconnect`) | `{startup}`, the display a reset restores; or `{method, display}` once the viewer has asked for that display. Used by the seat host, which holds the desktop lease for the change and confirms it on the seat's screen. |
| `seat.pad-visibility` | `devices` (instance paths of ViGEm pads and their devices) | `{session, devices: [{device, interfaces: [{path, opens}]}]}`; used by the seat host to check that the user's session cannot open a controller HidHide keeps in the seat. `opens` is `false` when refused, `null` when unknown. |
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
  "startupDisplay": { "width": 1280, "height": 720, "scale": 100 },
  "uptimeSeconds": 184.2,
  "lastError": null,
  "logPath": "C:\\Users\\you\\AppData\\Local\\Anode\\anode.log",
  "logError": null,
  "seat":  { "session": 3, "pid": 9120, "user": "you", "screen": {"width":1280,"height":720,"scale":100}, "cursor": {"x":640,"y":360} },
  "steam": { "steamExe": "D:\\STEAM\\steam.exe", "seatSession": 3, "runningSessions": [1],
             "runningOutsideSeat": true, "clientSession": 1, "signedIn": true,
             "summary": "Steam is running on your desktop (session 1)..." }
}
```

`state` moves through `starting` → `connecting` → `signing-in` → `starting-agent` → `ready`, and can
land on `detached` (seat alive, viewer disconnected), `stopping`, `stopped`, `error` or `logon-error`.
A viewer disconnect during startup becomes `error`, preserving the disconnect code and explanation
in `lastError`. Startup waits return that failure immediately. `logError` reports the most recent
log-write failure and clears after a successful write; `logPath` is the actual resolved destination.
`channel` is `main` for the installed Anode, or the development channel, such as `dev`, whose pipes
this daemon serves. `seat.screen` is the seat's display now, in physical pixels with its Windows
scaling in percent; `startupDisplay` is the display the seat started with, which a lease's end and
`display.set` with `reset` restore.

`lease` is the desktop lease as the requesting `agentId` sees it: its `summary` begins "You hold the
desktop lease" when that agent is the owner, and `queuePosition` gives its place in line. Without an
`agentId` it is reported as to any agent. MCP names each agent after its client and the folder the
client started the server in, such as "Claude Code in WebShop", unless `ANODE_AGENT_NAME` is set.

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
| `ping` | | `{session, pid, user, uptimeSeconds, screen:{width,height,scale}, cursor:{x,y}}` |
| `screenshot` | `maxWidth`, `format` (`png`\|`jpeg`), `quality` | `{data (base64), mimeType, width, height, sourceWidth, sourceHeight, bytes}` |

`width`/`height` are the returned image; `sourceWidth`/`sourceHeight` are the seat's real screen.
**Click coordinates always use the source size**, so downscaling a screenshot costs tokens, not
accuracy. The seat host and its workers use physical pixels at every Windows scaling, so the source
size is the display's real resolution.

### Display

| `op` | Arguments | Result |
| --- | --- | --- |
| `display.set` | `width` and `height` together, `scale`, or `reset: true`; optional `screenshot` and `maxWidth` | `{width, height, scale, effectiveWidth, effectiveHeight, previous, startup, changed, method, elapsedMs, screenshot?, screenshotError?, summary}` |

Only the fields given change. The width is even, 640-8192, the height 480-8192, and `scale` one of
100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450 or 500. The seat host asks the daemon
(`seat.display`) to change the display live through Remote Desktop's display-control channel,
measures the seat's screen until it shows the new display, and asks for a viewer reconnect at the new
size when it does not change live (`method` `live` or `reconnect`; `none` when nothing had to change).
A reconnect keeps the session and its apps. The operation needs the desktop lease and invalidates
observations; window IDs stay valid. A display other than the one asked for, or none, fails with
`errorCode: "display_not_applied"` and a `result` describing the actual display. Release or expiry
of the lease restores the startup display, and the next lease-gated operation waits for that; if it
is still restoring when the operation's deadline passes, it fails with `errorCode:
"display_restoring"` without acting. Clients allow the operation about three minutes. See
[Test other displays](DISPLAYS.md).

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

### Audio

| `op` | Arguments | Result |
| --- | --- | --- |
| `audio.status` | none | `{available, captureTested: false, device?, deviceId?, speakerRedirection?, error?, playbackState, playbackError, summary}` |
| `audio.listen` | `durationMs` (100-30000; default 5000) | `{mimeType: "audio/wav", data: base64, bytes, durationMs, sampleRate: 48000, channels: 2, bitsPerSample: 16, peak, silent, packets, discontinuities, timestampErrors, summary}` |
| `audio.play` | exactly one of `path` (absolute local `.wav`) or `data` (base64 WAV) | `{state: "playing", durationMs, summary}` when playback has started |
| `audio.stop` | none | `{state: "stopped", summary}` |

All except `audio.status` require the desktop lease. Invalid arguments are rejected before
lease acquisition or dispatch. Playback is asynchronous, one agent clip at a time, and ends on
completion, stop, lease release/expiry or host shutdown. `audio.stop` leaves browser/game sound
alone. After an uncertain play result, check status rather than replaying it.

Playback accepts 16-bit PCM WAV, mono/stereo, 8-96 kHz, up to 8 MiB and 120 seconds. Listening
records the next interval of the seat's output mix, preserving silent gaps. No packets or silent
samples produce a full-duration silent WAV; this does not prove an application produced sound.
`peak` is a linear full-scale amplitude (0-1). Discontinuities/timestamp errors are reported.

MCP exposes `seat_audio_status`, `seat_audio_listen`, `seat_audio_play`, and `seat_audio_stop`.
Listening returns a native `audio` content block plus a text block of metadata, with no duplicate
base64 or `structuredContent`. MCP added audio content in protocol `2025-03-26`, so a client that
negotiated `2024-11-05` receives the same WAV as an embedded `resource` blob (`audio/wav`) instead.
The CLI writes a new WAV file. Clients without audio-content support can use the CLI and process
the file. See [Audio](AUDIO.md) for setup and isolation.

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
| `steam.status` | | `{steamExe, seatSession, runningSessions, runningOutsideSeat, clientSession, signedIn, summary}` |
| `steam.launch` | `appId`, `args`, `exe`, `force`, `timeoutMs` | `{appId, session, pid, path, steamSession, bridged, note}` |
| `ps.list` | `windowedOnly` | `{session, processes:[{pid,name,title,started,memoryMb}]}` |
| `ps.kill` | `pid` or `name` | `{killed}` |

`steam.launch` never moves Steam. When the Steam client runs outside the seat, the seat host starts
the game's program in the seat (`bridged: true`, with its `pid` and `path`) and connects it to that
client, whose session is `steamSession`. The program, its arguments and working folder come from
Steam's launch configuration unless `exe` names a program, absolute or relative to the game's folder.
When Steam is not running, the seat host first starts it minimized in the parent session. Either way
it then asks the client what a game's `SteamAPI_Init` asks, a pipe and then the signed-in user, by
running `anode __steam-ready` in the seat through the bridge and without an app id, so Steam does not
count it as a game. Steam's own records name an account before a game could use it, and keep one after
an unclean exit. The game starts once the account is connected to Steam, five seconds later if it had
to wait, or at the deadline if the account is signed in offline; the answer is due within `timeoutMs`
(default 60000, less a margin), and the CLI and MCP server allow about two and three minutes. A client
already in the seat launches the game itself
(`bridged: false`). `force: true` also launches through a client in the seat, which takes Steam over
from any other session. `ps.kill` refuses any pid outside the seat's session.

The bridge works because Steam's client library finds its client through two named objects,
`Steam3Master_SharedMemFile` and `Steam3Master_SharedMemLock`, and accepts another name for them in
the `steam_master_ipc_name_override` environment variable. Windows keeps such names per session, so
the seat host links a name of its own in the seat's object namespace to the client's objects and
gives a bridged game that name, with `SteamAppId`, `SteamGameId` and `SteamOverlayGameId`. The rest of
the conversation uses handles Steam duplicates into the game, which works across sessions for the
same user. The links last as long as the seat host.

`run` also refuses direct `steam.exe`/`steam` commands and `steam://` URLs when Steam is running
outside the seat, because they would start a second client that takes Steam over. It refuses Chrome
and Edge without `--user-data-dir`, and http(s) links when one of them is the default browser, while
that browser runs outside the seat, whose profile lock would stop it; use `browser.open`. It always
refuses Android Studio's launchers (`studio64.exe`, `studio.exe`, `studio.bat`), which would share the
user's settings with any Studio on the desktop; use `android.studio`. This check does
not inspect shortcuts or wrapper scripts. Other applications may also reuse an existing instance in another
session; a `run` response confirms where the launch originated, not where every resulting window
will appear.

### Android and browsers

| `op` | Arguments | Result |
| --- | --- | --- |
| `android.status` | none | `{sdk, emulatorVersion, adb, adbServerRunning, avds: [{name, displayName, api, image, abi, screen, playStore}], emulators: [{serial, avd, pid, session, inSeat, startedBy, booted?}], studio: [{path, version}], summary}` |
| `android.emulator` | `action` `start` with `avd`, `gpu`, `readOnly`, `coldBoot`, `audio`, `waitSeconds`; `stop` with `serial`; `screenshot` with `serial`, `maxWidth`, `format`; `adb` with `serial`, `args`, `timeoutSeconds` | start `{serial, avd, pid, consolePort, booted, waitedSeconds, readOnly, gpu, log, summary}`; stop `{serial, stopped, method, summary}`; screenshot `{serial, screenshot: {data, mimeType, width, height, sourceWidth, sourceHeight}, summary}`; adb `{serial, exitCode, timedOut, stdout, stderr, summary}` |
| `android.studio` | `project` (absolute folder) | `{path, version, pid, profile, project, firstStart, summary}` |
| `browser.open` | `url` (http, https or `about:blank`), `browser` (`chrome` or `edge`) | `{browser, path, profile, url, pid, newProfile, summary}` |

`android.status` needs no lease and never starts adb's server; the others need the lease. Running
emulators come from the files emulators keep in `%LOCALAPPDATA%\Temp\avd\running\pid_<pid>.ini`, and
each is in the seat when its process runs in the seat's session. `stop`, `screenshot` and `adb` accept
only `emulator-NNNN` serials of emulators in the seat; `adb` refuses options before the command and
commands that act on adb's server or other devices, including after a `wait-for-*` prefix, and a
`forward` that would remove or take over another device's host end (checked with `adb forward --list`
first). `start` takes the first even console port from
5554 to 5682 whose adb port is free, starts adb's server, then runs
`emulator -avd NAME -port PORT -no-boot-anim` with `-read-only` unless `readOnly` is false,
`-no-snapshot-load` for `coldBoot`, `-gpu host` or the emulator's software mode, and `-no-audio` unless
`audio`. It waits for `sys.boot_completed` for up to `waitSeconds` (0-150, default 120) within the
request's deadline, and keeps the emulator's output in `android\<serial>.log` in Anode's state folder.
Clients allow `android.emulator` about three minutes.

`android.studio` starts the newest Android Studio with `STUDIO_PROPERTIES` pointing at
`%LOCALAPPDATA%\AnodeAndroidStudio\studio.properties`, which moves its config, system, plugin and log
folders there and sets `disable.android.first.run`, and with `ANDROID_HOME` set to the SDK when it is
unset. `browser.open` starts Chrome or Edge with `--user-data-dir` set to `%LOCALAPPDATA%\AnodeChrome`
or `AnodeEdge`, `--no-first-run`, `--no-default-browser-check` and `--new-window`. See
[Android apps and web consoles](ANDROID.md).

### Gamepad

| `op` | Arguments |
| --- | --- |
| `gamepad.attach` / `gamepad.detach` / `gamepad.reset` | `slot` (0-3) |
| `gamepad.set` | `slot`, `buttons` `{name: bool}`, `axes` `{lx,ly,rx,ry: -1..1}`, `triggers` `{lt,rt: 0..1}` |
| `gamepad.tap` | `slot`, `button`, `ms` |
| `gamepad.state` | |

With HidHide installed, `gamepad.attach` returns `{slot, attached, device, seatOnly, verifiedFromDesktop, summary}`:
the pad's instance path, whether it is kept inside the seat, and whether the daemon, in the user's
session, was refused when it tried to open it. When HidHide cannot keep it in the seat the attach fails
with `errorCode` `not_isolated` and nothing stays plugged in; without HidHide, `seatOnly` is `false`.
`gamepad.state` returns `{slots, isolation}`; `isolation` has `hidHide` (`active`, `not installed`,
`switched off`, `inverted` or `unavailable`), `seatSession`, `pads` (`device`, `owner`: `anode`, `seat`
or `outside`, `seatOnly`, `verifiedFromDesktop`), `otherViGEmPrograms`, `allowedEverywhere` and a `summary`. See
[the virtual controller](SECURITY.md#the-virtual-controller).

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
| `seat_audio_status` | `audio.status` | `seat_audio_listen` | `audio.listen` |
| `seat_audio_play` | `audio.play` | `seat_audio_stop` | `audio.stop` |
| `seat_display` | `display.set` | `seat_browser` | `browser.open` |
| `android_status` | `android.status` | `android_emulator` | `android.emulator` |
| `android_studio` | `android.studio` | | |

There are 41 tools. MCP assigns an agent ID (or uses `ANODE_AGENT_ID` from its environment),
remembers the token returned by `seat_lease` acquire or renew, and supplies both on desktop calls.
`anode_guide` returns the embedded operating guide without a daemon, setup or waiting behind long
tool calls.

Only `seat_start` and `seat_lease` with `action: "acquire"` start anything: with no daemon running
they launch a hidden one through the shared launcher, and they wait for the seat to be ready.
`seat_status` never starts a daemon. With none running it returns a successful result containing
`state: stopped`, `daemonRunning: false`, `agentId`, `ownerAgentId: null` and a `summary` that says
to acquire a lease; with one running it returns that daemon's status. `seat_capabilities`,
`seat_processes`, `steam_status`, `seat_audio_status` and `android_status` likewise report a stopped seat instead of starting one, and
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
`seat_screenshot`, `seat_wait`, `seat_capabilities`, `seat_processes`, `steam_status`,
`seat_audio_status`, `seat_audio_listen` and `android_status` are read-only (`readOnlyHint: true`).
`seat_windows`, `seat_observe`, `seat_screenshot`, `seat_wait` and `seat_audio_listen` return whatever
the seat's apps and web pages show, so they set `openWorldHint: true` to mark that
content as untrusted; the other read-only tools set it false. `seat_start` and `seat_hide` are additive
(`destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false`). Every other tool keeps
conservative hints (`destructiveHint: true`, `idempotentHint: false`, `openWorldHint: true`),
including `seat_show`, which puts the viewer on the user's screen. `annotations.title` repeats the
tool's title. Annotations describe effects; they are not approval overrides, and they never make a
timed-out action safe to replay. Titles and initialization instructions provide task-selection
guidance. See [For agents](FOR-AGENTS.md).

The server also offers four prompts, answered locally like `anode_guide`. None takes arguments.
`desktop_test` returns the lease workflow for the app or task the user names, `desktop_guide` returns
the full guide, `display_test` walks the app the user names through a set of resolutions and
scalings with `seat_display` and asks for a report of what breaks at each, and `android_test` tests
an Android app on an emulator in the seat: every screen, dark mode, font size, process death and
logcat. In Claude Code, `/mcp__anode__desktop_test` runs one (its `/` menu lists it
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
