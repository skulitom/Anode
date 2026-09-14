# Elevated certificate repair result

**September 13 update:** RDP-Tcp is listening after reboot. The historical failure
below is superseded by [post-reboot validation](2026-09-13-post-reboot-validation.md).
Child-session authentication is still being investigated.

Ran `bugreports\fix-rdp-listener.ps1` elevated on 2026-09-12 at 23:24 local time.
Added a native exit-code check after `icacls` so a failed key grant cannot be
reported as successful, and disposed the opened RSA handle before binding.

Completed:

- Backed up RDP-Tcp configuration to
  `artifacts\rdp-tcp-before-certificate-20260912-232407.reg`.
- Created the self-signed certificate in `Cert:\LocalMachine\My`:
  `<certificate-thumbprint>` (expires September 12, 2031).
- Granted NETWORK SERVICE Read on its private key:
  `C:\ProgramData\Microsoft\Crypto\Keys\a793123fab45907587694cb35325272b_ed858f8a-22a7-45db-bb1c-8a2b4726edcc`.
- Bound that certificate through `Win32_TSGeneralSetting.SSLCertificateSHA1Hash`.
  Readback confirms the thumbprint and hash type 3 (custom certificate).

`Restart-Service TermService -Force` failed with "Service ... stop failed".
TermService reports Running, but `qwinsta` still shows no RDP-Tcp listener and no
TCP listener exists on port 3389. LocalSessionManager event 17 was recorded again
at 23:24:08 with `0x80070005`. The script therefore exited 1, not success.

## Key access verified independently

A temporary scheduled probe ran as **NT AUTHORITY\NETWORK SERVICE** at 23:26.
It opened the new certificate's private key, signed a fixed diagnostic string
with RSA/SHA256, and verified the signature successfully. The probe did not export
the private key or send anything over the network. Its scheduled task was removed.

Result: `artifacts\rdp-key-access-check\result.json` contains `keyUsable: true`.
The key ACL has SYSTEM and Administrators FullControl, plus NETWORK SERVICE Read.
`sc qsidtype TermService` reports UNRESTRICTED.

This establishes that the NETWORK SERVICE account can use this particular key;
it does not establish that TermService completes TLS initialization. Missing
creator inheritance alone no longer explains the remaining failure, since this
certificate and key are explicitly installed, usable by that account, and bound.
Further diagnosis of the service-start access denial is still required.

## Service initialization traced

Further elevated diagnostics between 23:35 and 23:50 refine that conclusion:

- Process Monitor and service ETW events show that TermService **does stop**,
  with a successful exit, but the DCOM broker immediately starts a new process.
  This rapid transition makes `Restart-Service` report "stop failed". The new
  process then reports Running despite failing its internal initialization.
- SessionEnv can reach Running. TermService subsequently stops it while cleaning
  up the failed initialization. CertPropSvc and UmRdpService also start when
  requested explicitly. Their failure to remain running does not establish a
  dependency-service permission problem.
- The actual TermService process runs as NETWORK SERVICE and has its unrestricted
  `NT SERVICE\TermService` SID. SessionEnv and CertPropSvc service descriptors
  grant this SID start/stop rights. No speculative service ACL changes were made.
- At 23:50:08, RPC ETW recorded TermService PID 55872 calling LSM PID 1412 over
  local RPC, interface `ISessionManager`
  (`517f87fe-597a-4672-8555-6daf1c8c788d`), operation 3. The call used packet privacy
  and returned RPC status 5 (access denied). TermService propagated `0x80070005`
  back through `IConnectionManager`; LSM then logged event 17. This places the
  remaining failure before the certificate/listener initialization observed in
  the earlier file/registry trace. The exact access check remains unresolved.
- The only TermService `ACCESS DENIED` in the Process Monitor trace was a request
  for All Access to WinSock2 Parameters, immediately followed by a successful Read
  open. This fallback is not evidence that the WinSock registry ACL needs changing.

Diagnostic files (local, ignored by Git):

- `artifacts\rdp-trace\startup.pml` and `startup.csv`
- `artifacts\rdp-trace\rdp-events.json`, `rdp-com-events.json`
- `artifacts\rdp-trace\rdp-initialization.etl` and `rdp-initialization.json`
- `artifacts\rdp-token-result.txt`

Targeted SFC verification of termsrv.dll, lsm.dll, sessenv.dll, lsmproxy.dll, and
rdpcorets.dll found no integrity violations. DISM CheckHealth nevertheless reports
a repairable component store. `DISM /Online /Cleanup-Image /RestoreHealth /NoRestart`
was started elevated at 23:44:31, with `sfc /scannow` scheduled immediately after a
successful DISM result. Progress is recorded in
`artifacts\windows-component-repair-result.json`. At 23:51 it was still downloading
replacement components through Windows Update; no success is claimed yet.

Windows already has the CBS `RebootPending` registry key, and the last boot was
September 11 at 14:36 local time. No reboot was initiated by these diagnostics.

## DISM result, September 13 at 00:00

DISM finished cleanup and exited at 00:00:45 with `0x800f0915`
(`CBS_E_REPAIR_CONTENT_MISSING`; signed exit code -2146498283). The CBS summary
records 43 corrupt payload files and **zero repaired**. It downloaded content
through Windows Update but could not obtain everything required by the store.
The wrapper therefore did not proceed to the full `sfc /scannow`. Earlier targeted
SFC verification of the Remote Desktop files passed; it is not a full-system scan.

The DISM operation itself says it does not require a reboot. The separate,
CBS `RebootPending` marker observed while servicing was active was **no longer
present** after DISM exited. It therefore does not establish that Windows Update
still requires a restart. A Windows restart remains the next diagnostic step to
reload LSM (which has remained running since September 11), before pursuing
additional component repair sources or further permission changes. The exact cause
of LSM rejecting the local `ISessionManager` call is still unproven.

At 00:01, the newly published Anode setup successfully recognized TermService's
replacement process (55872 to 61992), then correctly failed the actual listener
check in 22.26 seconds. Port 3389 was not listening and `qwinsta` still listed only
services and console. Results are in `artifacts\setup-restart-validation.json`.

No service ACLs, COM ACLs, authentication levels, or firewall protections were
weakened during this investigation.

Transcripts:

- `artifacts\rdp-certificate-repair-result.txt`
- `artifacts\rdp-key-access-check-transcript.txt`

The certificate and binding are retained. The original unrelated probe key
`fbbe79b0699023bdafb3efe6d5eb84c1_ed858f8a-22a7-45db-bb1c-8a2b4726edcc` remains in
Crypto\Keys. No seat, Liftoff, or pilot was started during this repair.
