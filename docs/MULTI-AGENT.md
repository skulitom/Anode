# Multiple agents sharing Anode

Anode accepts several MCP/CLI clients, but they share **one Windows desktop**. Each agent
must acquire an exclusive desktop lease before screenshots, window/control inspection,
input, application launches, process termination, gamepad changes or command-job starts.
Status, capabilities, process listings and an agent's own job reads/cancellation do not need a lease.

This coordinates trusted agents under the same Windows identity. It is not authentication or
a security sandbox: files, accounts, network ports, applications and the desktop remain shared.
Use separate agent IDs for independent tasks. Agents deliberately using the same ID share ownership.

## MCP workflow

1. Call `seat_lease` with `{"action":"acquire"}`. It starts a hidden seat if needed.
2. Check capabilities, observe the desktop, act, then verify while holding the lease.
3. Call `seat_lease` with `{"action":"renew"}` before expiry, including during a long task.
4. Close your owned fixtures while holding the lease. Call `seat_lease` with
   `{"action":"release","cancelJobs":true}` to request cancellation of your command jobs
   and release the desktop. Omit `cancelJobs` to leave noninteractive jobs running.

The MCP server assigns a random agent ID and remembers its lease token. Tokens are automatically
attached to desktop calls; the model does not need to repeat them. `seat_lease` with
`{"action":"status"}` reports the caller ID, current owner, time remaining and whether an
operation is still running. Status never starts a daemon or seat and does not expose the owner's token.

The default lifetime is **120 seconds**, configurable through `ttlSeconds` from **10 to 600**
on acquire/renew. There is no automatic renewal while an agent is idle. Acquisition fails promptly
with `seat_busy` when another agent owns the desktop; wait and retry acquisition. There is no FIFO
reservation queue or automatic takeover. Repeating acquire for the current owner recovers its token
without extending its lifetime; use renew to extend it.

## CLI workflow

Set a unique ID in the shell used by this agent and retain the returned token:

```powershell
$env:ANODE_AGENT_ID = 'build-agent-1'
$lease = anode lease acquire --ttl 120 | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken

anode windows | Out-Host
anode shot seat.png | Out-Host
anode lease renew --ttl 120 | Out-Host
# Close your owned fixtures before releasing.
anode lease release --cancel-jobs | Out-Host
Remove-Item Env:ANODE_LEASE_TOKEN
```

Alternatively, supply options **before** the command:
`anode --agent build-agent-1 --lease RETURNED_TOKEN windows`.
Prefix options do not consume literal text or the arguments of launched programs.
Job reads and cancellation need `ANODE_AGENT_ID`, but not a live lease.

## Disconnects, timeouts and cleanup

- Closing a CLI or MCP connection does not release a workflow's lease or cancel its jobs.
  The lease expires when renewal stops. Set `ANODE_AGENT_ID` in the MCP process environment
  to resume the same identity after restarting that process. Reacquire, then use
  `seat_job {"action":"list"}` to recover jobs; never replay an uncertain command start.
- Expiry prevents new actions. An action already admitted may finish; another agent cannot
  acquire until it finishes. Queued requests are checked again at dispatch, so an expired or
  replaced token cannot issue later input. Expired leases cannot be renewed.
- Release/expiry invalidate all window and control references, release keyboard/mouse input
  held by Anode and detach this host's virtual controllers. Idle expiry is checked every second.
  Always acquire fresh references after reacquisition. Virtual controllers remain machine-wide.
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

## Protocol and upgrades

Direct pipe clients send `agentId` and `leaseToken` alongside desktop operation arguments.
The `lease` operation accepts `action`, `ttlSeconds` and `cancelJobs` as above. Acquisition
requires an agent ID; renew/release require that ID and the current token. Ownership errors
include `errorCode` (`seat_busy`, `lease_expired`, `stale_lease`) and public lease state in `result`.
MCP preserves these fields in `structuredContent` on errors.

This is a breaking change for unattended desktop commands: unowned input is refused. Upgrade
the executable, daemon/seat host and client together, and update the installed agent skill.
Already-running processes keep the previous behavior until restarted. Stop the existing seat
only when its work can be closed, then start the freshly built executable.

Multiple independent desktops are not implemented. Windows supports only one active, connected
child session per system; separate simultaneous desktops require a different backend, such as
separate Windows VMs/machines or Remote Desktop Services.
[Microsoft child-session documentation](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions).
