# `anode setup` leaves the machine without an RDP listener when TermService was already running

**Found by:** Claude (Haltere session), 2026-09-12, Windows 11 Pro 25H2 build 26200, Anode `dist\anode.exe` built 21:04.

## What happened

`anode doctor` reported the machine ready after `anode setup --fps 60 --gpu`:

```
[ok  ] Remote Desktop host  fDenyTSConnections = 0
[ok  ] Child sessions       enabled
```

but every `anode start` ended with `The seat did not become ready in time` and `anode status` showed
`state stopped`, `lastError null`. Running the daemon in a shell (`anode up`) logged the real reason:

```
[daemon] connecting the viewer to a child session at 1920x1080
[daemon] [stopped] The seat is gone. The Remote Desktop host refused the loopback connection. ...
```

The host refused because nothing was listening:

```
> netstat -an | findstr :3389          (no output)
> qwinsta
 SESSIONNAME   USERNAME   ID  STATE   TYPE
 services                  0  Disc
>console       example       1  Active
```

No `RDP-Tcp ... Listen` entry. `Get-Service TermService` said Running, `SessionEnv` and `UmRdpService`
Stopped. The setup log line was `Start the Remote Desktop Services service: TermService started`, but
the service was already running before setup (it ran with `fDenyTSConnections = 1`), so it never
re-read the setting and never created the listener.

## Root cause on this machine (update)

A plain `Restart-Service TermService -Force` did **not** help. The Local Session Manager log
(`Microsoft-Windows-TerminalServices-LocalSessionManager/Operational`, event 17) showed the listener
failing at every service start:

```
Remote Desktop Service start failed. The relevant status code was 0x80070005.
```

`Cert:\LocalMachine\Remote Desktop` was empty and its registry store key absent, i.e. the listener could
not create its self-signed certificate. The reason was the ACL of the CNG key folder
`%ProgramData%\Microsoft\Crypto\Keys`: only `Everyone: Read` (plus SYSTEM/Administrators full), where
Windows' default also grants write on the folder itself (`MachineKeys` next to it still had
`Everyone: Read, Write`). TermService runs as NETWORK SERVICE and could not create the private key.

Update: granting `Everyone` write on the folder (done by the owner) was not enough. A probe as an
ordinary user could then *create* a CNG machine key but got "Access is denied" *deleting* it, so the
folder also lacks the inheritable entries that normally give a key's creator rights on its own key
file (`CREATOR OWNER` / the KSP-assigned DACL); the service creates a key it cannot use afterwards.
`Crypto\Keys` held 0 files on this machine (the RSA `MachineKeys` folder next to it had 3), which
suggests the folder was recreated or cleaned at some point with a wrong ACL.

Workaround that does not depend on the folder ACL: create the RDP certificate as an administrator,
grant `NT AUTHORITY\NETWORK SERVICE` read on that one key file, bind it to the listener
(`Win32_TSGeneralSetting.SSLCertificateSHA1Hash`) and restart TermService. Script: `fix-rdp-listener.ps1`
in this folder. (A stray probe key file `fbbe79b0699023bdafb3efe6d5eb84c1_ed858f8a-...` was left in
`Crypto\Keys` by the probe and can be deleted by an administrator.)

Originally proposed fix (to be run by the machine's owner in an elevated shell; the agent could not apply it):
`icacls "%ProgramData%\Microsoft\Crypto\Keys" /save keys-acl-backup.txt` to keep the current ACL, then
`icacls "%ProgramData%\Microsoft\Crypto\Keys" /grant "*S-1-1-0:(RX,W)"`, then
`Restart-Service TermService -Force`. Expected: `qwinsta` lists `RDP-Tcp ... Listen`, the `Remote Desktop`
certificate store gets a self-signed certificate, and the seat comes up.

So `anode setup` was not wrong to only flip the registry values, but `doctor` reporting "ready" on the
registry value alone hid a machine problem that took the event log to find. A listener probe in `doctor`
plus a pointer to the LSM event 17 (`0x80070005` = certificate/key permissions) would have saved an hour.

## Suggested changes

1. In `setup`, when `fDenyTSConnections` changes from 1 to 0 and TermService is already running,
   restart it (or stop and start it) instead of only ensuring it is started. Tell the user the service
   was restarted.
2. In `doctor`, check for the actual listener rather than only the registry value: `WTSEnumerateListeners`
   / `WTSQueryListenerConfig`, or a TCP probe of the configured `PortNumber` on loopback. Report
   `[FAIL] Remote Desktop listener  nothing is listening on port 3389 (restart TermService)`.
3. Map disconnect reason 1800 to the listener check in the message the daemon stores, so `status`
   shows it (see the separate report on `lastError` and the detached daemon's log).
