# Startup performance

The startup changes after 0.11.1 remove fixed waits in Anode's orchestration. Windows still owns
RDP authentication and desktop creation; time spent entering a password is outside these timings.

## What changed

- Before launching, CLI and MCP check for the daemon's mutex and attempt its pipe immediately
  when it is absent. A daemon takes this mutex before opening its pipe, so callers retain the
  original connection wait while it starts. A listening pipe is tried even without the mutex.
- Startup callers wait for state changes instead of polling every 300 ms. Ready, failure and Stop
  wake all waiting callers. A Stop generation prevents an old caller following a later restart.
- MCP attempts a connection immediately after daemon launch, removing its initial 400 ms sleep.
  Failed daemon/host connection attempts retain bounded waits and use a 50 ms retry gap.

The 500 ms host-reuse window after login is retained, including on a new desktop. The parent
still resolves the child session and verifies the host's returned session before
accepting it. The pointer guard, isolated channels, detached launcher and input timeout rules
are unchanged.

## Measurements

Measured locally on Windows 11 on 2026-10-02, comparing the published-build source at 0.11.1
with a Release build of these changes. These are development measurements, not performance
guarantees across machines.

| Operation | 0.11.1 median | Updated median | Samples per build |
| --- | ---: | ---: | ---: |
| CLI `status --json`, absent daemon, including process startup | 934 ms | 126 ms | 8 |
| MCP `seat_status`, absent daemon, already initialized process | 1,007 ms | 0.21 ms | 8 |

Both paths reported a stopped daemon throughout and never launched a seat. CLI measurements
used a stopwatch around the complete process, including collecting its response;
the expected stopped exit code was 1. MCP measurements timed each request through its reply
in a single initialized process per build, excluding initialization. The first updated MCP call
took 25.9 ms; subsequent calls were below 1 ms. Each build used a separate otherwise unused
channel. These measure the connection-discovery step also used before cold startup, not the
complete time to a usable Windows desktop.

The final live cold-start check reached a verified host in session 6 in 1,017 ms after
`starting-agent` (16:19:04.744 to 16:19:05.761). The 0.11.1 reference was 887 ms in a different
session. This is one sample per build and does not demonstrate a faster post-login phase;
the measured improvement is in discovery before launch. An exploratory build that skipped the
host-reuse window had one accepted launch with no host appearing. That shortcut was removed
before the final live check; the scheduler handoff itself remains unchanged.

## Validation and reproduction

Build and run the quick checks as described in [development testing](DEVELOPMENT-TESTING.md).
The 86 checks include private-pipe coverage for absent and starting daemons, hosts without a
mutex, retained hosts whose pipe becomes available late, cancellation before host launch,
concurrent startup callers, terminal errors, and Stop followed immediately by a new start.
They do not create a real seat.

For discovery measurements, use freshly built executables and unique unused channels. Time
`anode --channel <unused> status --json` repeatedly, retaining the stopped result and exit code.
For MCP, initialize `anode --channel <unused> mcp`, then time repeated `seat_status` calls in
that same process and close stdin afterward. Never use `start` or `seat_lease` for this benchmark.

For live measurements, first arrange for the existing seat to close, then start the candidate
on the dev channel. Record daemon log timestamps from `starting-agent` to `ready`, which exclude
manual sign-in time. For reconnect, record the child session and host process, reconnect the
viewer, and verify that both are retained without a second host launch. Run the disposable
desktop fixture in the ready seat with a lease, then clean up and release it.
