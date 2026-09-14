# Validation of the September 12 fixes

**September 13 update:** the listener recovered after reboot. See
[post-reboot validation](2026-09-13-post-reboot-validation.md) for additional COM,
logging and startup-diagnostic fixes and the remaining authentication failure.

The original reports are preserved. Changes are uncommitted in this working tree;
`dist\anode.exe` was rebuilt after the fixes.

## Application changes

- `doctor` probes both loopback address families on the configured RDP port. A missing
  listener fails readiness even when the host registry value and child-session flag pass.
  The fix text points to LocalSessionManager event 17 and the possible certificate/key
  permission cause of `0x80070005`.
- Setup starts a stopped service or restarts a running service when enabling the host
  or repairing a missing listener. It restores previously running dependent services,
  verifies listener availability, and preserves elevated setup failures in its exit code.
  A follow-up fix uses the service process ID to recognize immediate DCOM reactivation:
  observing a replacement process is sufficient even when polling misses Stopped.
  Starting a service also tolerates Windows winning the race to start it first.
- Startup disconnects preserve their numeric reason in `lastError`, cancel pending
  bring-up, and fail promptly. CLI/MCP startup polling also stops on terminal states
  and transport errors. Existing daemons recheck prerequisites before starting a seat.
- Detached launches explicitly forward their state directory. Log writes retry sharing
  conflicts; the first failure reaches stderr and `status.logError` exposes failures
  even without a console. Logs identify the process, and daemon startup logs its path.

## Verification

- Debug build: no warnings or errors; 24 quick checks passed.
- Published Release build: 24 quick checks passed; all 27 checks in
  `selftest --no-gamepad` passed, including the actual Task Scheduler logging probe.
- Private pipes cover startup disconnects and terminal status/error propagation.
  Disposable subprocesses cover concurrent logging and visible write failures.
- On the affected machine, published `anode start --hidden` returned exit code 3 with
  the missing-listener diagnosis in 1.95 seconds.
- The reported `kill` exit was not reproduced against the previously running daemon:
  stopping an absent seat left the daemon answering status. It was then explicitly
  quit to publish the updated executable.
- No live seat or gamepad input test was performed. The daemon is currently stopped.
- The follow-up restart check passed the Debug build and all 24 quick checks,
  including regression cases for a replacement process, an ordinary stop, an
  unchanged running process, and an unavailable process ID. These checks do not
  change real services. The Release executable was republished and all 24 quick
  checks passed again. Live listener repair still requires the Windows fault to
  be resolved; application self-tests alone do not establish that a seat can start.
- Live elevated setup using that published executable on September 13 at 00:01
  recognized the TermService replacement (PID 55872 to 61992), reached the listener
  probe, and returned exit 1 after 22.26 seconds with the missing-listener diagnosis.
  It no longer reports a spurious stop timeout. Port 3389 and RDP-Tcp remain absent.

## Machine repair attempted at the user's request

The original ACL for `C:\ProgramData\Microsoft\Crypto\Keys` was saved to:

`C:\DEV\Anode\artifacts\keys-acl-backup-20260912-230058.txt`

The requested `icacls /grant "*S-1-1-0:(RX,W)"` succeeded. The new grant applies only
to the folder itself; child-key permissions were not recursively changed.

`Restart-Service TermService -Force` failed. An elevated ServiceController retry
timed out waiting for the service to stop. The service subsequently reports Running,
but `qwinsta` still has no RDP-Tcp listener. The latest LocalSessionManager event 17
(23:01:33 local time) still reports `0x80070005`. The proposed ACL change therefore
did not resolve listener creation; the underlying access-denied failure still needs
diagnosis. Application setup does not apply this crypto ACL change automatically.

Transcripts are in `artifacts\repair-crypto-keys-result.txt` and
`artifacts\restart-termservice-result.txt`. To restore the saved folder ACL from an
administrator PowerShell if desired:

```powershell
icacls.exe 'C:\ProgramData\Microsoft\Crypto' /restore 'C:\DEV\Anode\artifacts\keys-acl-backup-20260912-230058.txt'
```
