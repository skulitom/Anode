# Guide: test your desktop app with Claude Code, without losing your mouse

You are building a Windows app. You want Claude Code to build it, click through it and tell you what
broke, while you keep typing in your editor. Anode runs the app in the seat, a second Windows
session with its own screen, pointer and focus, so nothing the agent does lands on your desktop.

![An agent clicking through Calculator and ticking off a task in a sample web app, inside Anode's background seat](https://raw.githubusercontent.com/skulitom/Anode/main/assets/demo.gif)

<sub>Recorded inside the seat with Anode 0.11.0, from real MCP tool calls. Idle time between calls is
cut, and the nine clicks, which took 0.4 s, are shown four times slower. It is the same loop as below,
on Calculator instead of your app.</sub>

## Once per machine

In PowerShell, after [installing Anode](INSTALL.md):

```powershell
anode setup | Out-Host              # one administrator prompt
anode configure claude | Out-Host   # registers Anode and the anode-desktop skill with Claude Code
```

Restart Claude Code and check that `/mcp` lists `anode`. (Using the Claude Code plugin instead? See
[Connecting agents](CONNECTING-AGENTS.md#1-claude-code).)

## Ask for the test

Name the app, the steps and what counts as a pass. For example:

> Build C:\src\Invoices with `dotnet build`. Then use Anode to open
> bin\Debug\net10.0-windows\Invoices.exe in the background seat, add an invoice for "Sample Ltd"
> with one line of 120.00, check that the total reads 120.00, and show me a screenshot. Keep the
> viewer hidden and close the app when you're done.

Or run the guided prompt `/mcp__anode__desktop_test` and name your app.

## What the agent does

The agent builds with its ordinary shell (`dotnet build C:\src\Invoices`), since a build needs no
desktop. These are the MCP calls that follow, in order. Each one is a tool call with the arguments
shown; IDs such as `w_…`, `s_…` and `e12` come from the previous reply, and the control IDs are
examples.

```json
{"tool": "seat_lease", "arguments": {"action": "acquire"}}
{"tool": "seat_capabilities", "arguments": {}}
{"tool": "seat_run", "arguments": {"path": "C:\\src\\Invoices\\bin\\Debug\\net10.0-windows\\Invoices.exe"}}
{"tool": "seat_windows", "arguments": {"query": "Invoices"}}
{"tool": "seat_observe", "arguments": {"windowId": "w_RETURNED_ID"}}
{"tool": "seat_element", "arguments": {"snapshotId": "s_RETURNED_ID", "elementId": "e12", "action": "set_value", "value": "Sample Ltd"}}
{"tool": "seat_observe", "arguments": {"windowId": "w_RETURNED_ID"}}
{"tool": "seat_element", "arguments": {"snapshotId": "s_NEW_ID", "elementId": "e14", "action": "set_value", "value": "120.00"}}
{"tool": "seat_observe", "arguments": {"windowId": "w_RETURNED_ID"}}
{"tool": "seat_element", "arguments": {"snapshotId": "s_NEW_ID", "elementId": "e15", "action": "invoke"}}
{"tool": "seat_wait", "arguments": {"windowId": "w_RETURNED_ID", "automationId": "TotalLabel", "textContains": "120.00", "waitMs": 10000}}
{"tool": "seat_screenshot", "arguments": {"maxWidth": 1000}}
{"tool": "seat_window", "arguments": {"windowId": "w_RETURNED_ID", "action": "close"}}
{"tool": "seat_lease", "arguments": {"action": "release", "cancelJobs": true}}
```

- **Accessible controls first.** `seat_observe` returns the window's controls with the actions each
  one supports; `seat_element` acts on one of them. An observation is used up by an action, so the
  agent observes again before the next one.
- **Waits, not sleeps.** `seat_wait` returns as soon as the total shows 120.00, or reports that it
  didn't within the time given.
- **Pixels when there is no control.** For a custom-drawn canvas or a game, the agent takes a
  `seat_screenshot` and uses `seat_click` with coordinates from it. `seat_click` takes the seat's
  full-size pixels, so a point read from a screenshot scaled with `maxWidth` is scaled back first
  (`x * sourceWidth / width`, the same for `y`).
- **Jobs the test itself needs.** A local server, or a long build the desktop test depends on, can run
  in the seat with `seat_exec`; read its output with `seat_job` until the reply says `finished: true`
  ([command jobs](DEVELOPMENT-TESTING.md#command-jobs)).

## The same from a script

Every step has a CLI command, so a test you like can become a script. It needs an agent ID and the
lease token, as in [desktop leases](MULTI-AGENT.md):

```powershell
dotnet build C:\src\Invoices
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$env:ANODE_AGENT_ID = 'invoices-test'
$lease = anode lease acquire | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
anode run C:\src\Invoices\bin\Debug\net10.0-windows\Invoices.exe | Out-Host
anode windows --query Invoices | Out-Host
anode inspect w_RETURNED_ID --html invoices.html | Out-Host
anode element s_RETURNED_ID e12 set_value --value "Sample Ltd" | Out-Host
anode inspect w_RETURNED_ID | Out-Host
anode element s_NEW_ID e14 set_value --value 120.00 | Out-Host
anode inspect w_RETURNED_ID | Out-Host
anode element s_NEW_ID e15 invoke | Out-Host
anode wait w_RETURNED_ID --automation-id TotalLabel --text 120.00 | Out-Host
anode shot invoices.png | Out-Host
anode window w_RETURNED_ID close | Out-Host
anode lease release --cancel-jobs | Out-Host
```

Build before `lease acquire`: the lease ends 120 seconds after the last desktop command, and a build
isn't one, so a longer build would leave `anode run` failing with `lease_expired`. For a script with
long steps between desktop commands, `anode lease acquire --ttl 600` keeps it longer.

Each `element` needs the snapshot ID from the `inspect` just before it. `inspect --html` writes a
report you can search, with the screenshot and each control's bounds, which helps when you write the
test. `wait` exits 3 when the control never matched; check `$LASTEXITCODE` after each command.

## Why your mouse stays yours

- The app runs in the seat's session, and input goes there: your pointer, focus and keyboard are
  not used.
- A program in the seat that moves its own cursor, as games do, can't move your real pointer:
  Anode gates that call ([details](TROUBLESHOOTING.md#my-real-pointer-jumps-while-something-runs-in-the-seat)).
- The viewer stays hidden unless you run `anode show`. **Ctrl+Alt+Shift+K** stops the seat at any
  time.

The seat runs as you and shares your files, so a test that writes files writes your files. Point
the app at a scratch folder, and see [security](SECURITY.md) for what the seat does not isolate.

## When it doesn't work

- **No controls in the tree.** Some apps draw everything themselves; use screenshots and
  `seat_click`. WinForms and WPF expose rich trees; [desktop tools](DESKTOP-TOOLS.md) covers the
  actions.
- **Clicks or typing have no effect.** Run `anode capabilities`: it reports known input blockers
  ([capture works, but input does not](TROUBLESHOOTING.md#capture-works-but-focus-and-input-do-not)).
- **The app opened on your desktop instead.** Single-instance apps hand a second start to the copy
  already running; close yours, or test a build with its own settings.
- More: [development and testing](DEVELOPMENT-TESTING.md), [troubleshooting](TROUBLESHOOTING.md).
