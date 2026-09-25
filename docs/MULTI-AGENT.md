# Multiple agents sharing Anode

Anode accepts several MCP/CLI clients, but they share **one Windows desktop**, so they take turns.
An agent holds an exclusive desktop lease while it takes screenshots, inspects windows or controls,
sends input, launches or ends applications, changes gamepads or starts command jobs. Status,
capabilities, process listings and an agent's own job reads/cancellation do not need a lease.

This coordinates trusted agents under the same Windows identity. It is not authentication or
a security sandbox: files, accounts, network ports, applications and the desktop remain shared.
Use separate agent IDs for independent tasks. Agents deliberately using the same ID share ownership.

## How agents take turns

- **Taking the desktop.** Over MCP, desktop tools take the lease themselves when the desktop is
  free, so an agent working alone never calls `seat_lease`. `seat_lease` with
  `{"action":"acquire"}` does the same explicitly and also starts a hidden seat if needed; desktop
  tools never start one. The MCP server keeps the token and attaches it to desktop calls.
- **Waiting in line.** When another agent has the desktop, acquire waits in a first-come line for
  up to `waitSeconds` (0-300; default 30 over MCP, 0 for the CLI). An agent keeps its place only
  while it keeps asking: MCP and the CLI ask about once a second, and a place is dropped 5 seconds
  after the last ask, so the desktop never goes to an agent that gave up or went away. When the
  wait ends, calling acquire again within 5 seconds keeps the place. An acquire with
  `waitSeconds: 0` never joins or jumps the line.
- **Keeping it.** Each desktop action extends the lease to a full lifetime from when it starts
  (`ttlSeconds`, 10-600, default 120). An agent that keeps working never renews; `renew` is for
  pausing longer than the lifetime without acting. Nothing revives a lease that already expired.
- **Handing it on.** Release when finished. While others wait, desktop results tell the owner
  (MCP adds a note; pipe responses carry `waitingAgents`). Ending an MCP session releases its lease
  when the server generated its identity, because nothing could resume it; with a stable
  `ANODE_AGENT_ID` the lease stays until it expires, so a restarted session can recover it.
- **Seeing who has it.** `seat_lease` with `{"action":"status"}`, `seat_status`, `anode status` and
  `anode lease status` report the owner, its name, the time left and the line. An agent asking
  about a lease it holds is told "You hold the desktop lease", so it never mistakes its own lease
  for another agent's. MCP names an agent after its client and the project folder the client started
  it in, for example "Claude Code in WebShop" or "Codex in DroneSim", because every session of one
  client has the same client name; `ANODE_AGENT_NAME` overrides that. The viewer's footer shows the
  owner too, and the seat's log records each time the desktop is taken, released or expires. Status
  never starts a daemon or seat and never exposes another agent's token.

## MCP workflow

1. Call `seat_status` to see whether a seat is running; it never starts one.
2. Call `seat_lease` with `{"action":"acquire"}`. It starts a hidden seat if needed and waits in
   line if another agent is working.
3. Call `seat_capabilities` for capture and input blockers, then observe the desktop, act and
   verify. Each action keeps the lease; renew only if you pause for longer than its lifetime.
4. Close your owned fixtures, then call `seat_lease` with `{"action":"release","cancelJobs":true}`
   to request cancellation of your command jobs and hand the desktop on. Omit `cancelJobs` to
   leave noninteractive jobs running.

If an agent calls a pointer, keyboard or gamepad tool without a lease and Anode takes one for it,
that call is refused with a request to observe first: the input was aimed at a screen the agent
saw before another agent may have changed it. Observation, launch, window, control and job tools
proceed at once. Only acquisition and `seat_start` start a seat over MCP. Status, capabilities,
process and Steam diagnostics report a stopped seat instead.

## CLI workflow

Set a unique ID, and optionally a name, in the shell used by this agent and retain the returned token:

```powershell
$env:ANODE_AGENT_ID = 'build-agent-1'
$env:ANODE_AGENT_NAME = 'Build agent'
$lease = anode lease acquire --ttl 120 --wait 60 | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken

anode windows | Out-Host
anode shot seat.png | Out-Host
# Close your owned fixtures before releasing.
anode lease release --cancel-jobs | Out-Host
Remove-Item Env:ANODE_LEASE_TOKEN
```

`--wait` waits in line and reports the first refusal on stderr; without it, a busy desktop fails
at once with `seat_busy`. Each desktop command extends the lease; `anode lease renew --ttl 120`
covers a longer pause. Alternatively, supply options **before** the command:
`anode --agent build-agent-1 --lease RETURNED_TOKEN windows`. Prefix options do not consume literal
text or the arguments of launched programs. Job reads and cancellation need `ANODE_AGENT_ID`, but
not a live lease. The CLI never takes a lease implicitly.

## Disconnects, timeouts and cleanup

- Closing a CLI connection does not release a workflow's lease or cancel its jobs; the lease
  expires when its owner stops acting and renewing. An MCP session with a generated identity
  releases on exit; set `ANODE_AGENT_ID` in the MCP process environment to keep and resume the same
  identity instead. Reacquire, then use `seat_job {"action":"list"}` to recover jobs; never replay
  an uncertain command start.
- Expiry prevents new actions. An action already admitted may finish; another agent cannot
  acquire until it finishes. Queued requests are checked again at dispatch, so an expired or
  replaced token cannot issue later input. Expired leases cannot be renewed.
- Release/expiry invalidate all window and control references, release keyboard/mouse input
  held by Anode and detach this host's virtual controllers. Idle expiry is checked every second.
  Always acquire fresh references after reacquisition. Virtual controllers stay inside the seat
  only when HidHide is installed.
- Job listing, reading and cancellation are restricted to the recorded agent ID. Release with
  `cancelJobs` requests cancellation only for that agent's running jobs and descendants. Job
  output survives until the host exits or its bounded history evicts the completed job.
- A lease governs Anode commands, not autonomous application behavior. Apps and noninteractive
  jobs can continue running after release; only leave jobs running when that is appropriate.
  Window/process tools act on the shared desktop while leased; verify that routine cleanup
  targets your own fixture. `seat_run` reports the launching agent ID but does not promise that
  a reused application instance belongs exclusively to it.
- `seat_stop`, `anode kill`, the tray/viewer Stop and the emergency hotkey still stop **the entire
  seat** immediately without a lease. Use them when the whole seat should stop. Human viewer
  control can also interrupt the agent's workflow.
- When a person quits Anode, the seat and its lease normally end with it. An MCP server still
  holding the old token reports "Anode is not running or your lease ended", forgets the token and
  does not restart anything. Acquire again only when desktop work should continue, then observe
  afresh.

## Protocol and upgrades

Direct pipe clients send `agentId`, `leaseToken` and optionally `agentName` (1-64 printable
characters) alongside desktop operation arguments. The `lease` operation accepts `action`,
`ttlSeconds`, `waitSeconds` and `cancelJobs` as above. A `waitSeconds` above zero on acquire keeps
or takes a place in line for 5 seconds; the caller repeats the request to keep waiting. `startSeat:
false` on acquire refuses to start a stopped seat, which is how MCP takes a lease on an agent's
behalf. Acquisition requires an agent ID; renew/release require that ID and the current token.
Ownership errors include `errorCode` (`seat_busy`, `lease_expired`, `stale_lease`) and public lease
state in `result`: `ownerAgentId`, `ownerName`, `expiresInMs`, the `waiting` line and the caller's
`queuePosition`. MCP preserves these fields in `structuredContent` on errors.

Upgrade the executable, daemon/seat host and client together, and update the installed agent skill.
Already-running processes keep the previous behavior until restarted. Stop the existing seat
only when its work can be closed, then start the freshly built executable.

Multiple independent desktops are not implemented. Windows supports only one active, connected
child session per system; separate simultaneous desktops require a different backend, such as
separate Windows VMs/machines or Remote Desktop Services.
[Microsoft child-session documentation](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions).
