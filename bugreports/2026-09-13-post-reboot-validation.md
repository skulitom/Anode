# Post-reboot validation, September 13

## Listener recovery

After the 00:29 Windows boot, `qwinsta` shows `rdp-tcp ... Listen`, and port 3389
accepts connections on IPv4 and IPv6. Every `anode doctor` prerequisite passes.
The prior LocalSessionManager access-denied listener-start failure is no longer
reproducing. The retained certificate and ACL repairs are described in the
September 12 reports; no additional machine permissions were changed here.

## Additional Anode faults corrected

- `IMsRdpExtendedSettings` is an IUnknown interface, not IDispatch. The old
  declaration could write the OCX Server property instead of enabling child mode.
  Its vtable and VARIANT-by-reference signature now match the installed mstscax
  type library. Child mode must read back as true before Connect.
- COM callbacks with FOUT/FRETVAL parameters return through IDispatch's result
  VARIANT. Close confirmation, public-key acceptance and reconnect action now
  use that contract. A native IDispatch regression checks the real event sink.
- COM exceptions are unwrapped. Disconnects use GetErrorDescription plus the
  numeric and extended reason. Misleading fallback descriptions were corrected.
- The packaged Codex caller and Task Scheduler daemon had different physical
  AppData views. Procmon showed successful daemon writes in the ordinary profile
  while the caller read its package's LocalCache copy. Anode now resolves the
  physical state directory through a disposable file handle before forwarding it.
  Scheduled startup entries are visible in the caller's shared log.
- Windows can record a fatal handshake failure while the OCX stays connecting
  and delays OnDisconnected. Anode now checks the specific terminal SSL event for
  its own PID and current attempt, surfaces the code, and cancels startup. Stale
  events, other clients, and nonterminal transitions are excluded. Unavailable
  event logs leave the normal callback path intact.

## Remaining live blocker

The corrected control reaches child listener `7A78855482A04FA781DC`. Windows then
fails TsSslEventStartHandshakeFailed with `0x80004005`; no child session appears.
A separate minimal WinForms RDP client using only Server, desktop size,
EnableCredSspSupport and ConnectToChildSession reproduces this. The failure is
therefore not specific to Anode's optional settings or event sink. Its underlying
Windows cause remains unproven.

The user confirmed that the post-reboot Windows sign-in used PIN/Windows Hello.
The initial suggestion was to sign out and sign back in using the account password.
The user asked for an alternative that leaves the desktop signed in. See
[the subsequent sign-in investigation](2026-09-13-sign-in-without-parent-logoff.md).
No password was collected or requested in chat.

Debug `anode start --hidden` returned exit 1 with event 226 and `0x80004005` in
approximately three seconds. Status preserved that reason and the physical log
path, replacing the previous 150-second CLI timeout for this failure.
The final published executable reproduced this result in 3.06 seconds, exit 1.
Its status and timing are saved in `artifacts/published-post-reboot-status.json`
and `artifacts/published-post-reboot-validation.json`.

Some test daemons remained alive after acknowledging Quit while the native control
was stuck connecting. They were force-closed after verifying their process identity
and that no child session existed. This shutdown behavior still needs a retest
after successful authentication.

## Verification and artifacts

- Debug build: zero warnings/errors; all 27 quick checks passed.
- Published Release: build script and all 27 quick checks passed.
- All 30 published `selftest --no-gamepad` checks passed, including the actual
  Task Scheduler logging test under LocalAppData. Doctor prerequisites pass;
  its summary now distinguishes prerequisite checks from verified seat sign-in.
- Native extended-setting readback and native event-sink invocation pass.
- Event-data regressions reject stale and unrelated handshake failures.
- Live seat startup, in-seat capture, Liftoff and the pilot remain unverified.
- Local traces and reproduction source are in `artifacts/rdp-trace` and
  `artifacts/RdpProbe`. These ignored diagnostics have not been uploaded.
- All diagnostic traces, test clients and Anode daemons are stopped. RDP-Tcp
  remains listening; the parent console session remains active.

References: Microsoft [child-session API](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions),
[extended settings](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings),
and [PIN limitations](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip#limitations-of-child-session-mode).
