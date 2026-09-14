# Seat sign-in without signing out the parent desktop

## Findings on 2026-09-13

The parent console is session 3 and RDP-Tcp is listening after the earlier reboot.
The user confirmed PIN/Windows Hello sign-in and asked to avoid Windows login.

A minimal child-session client with `DisableCredentialsDelegation=true` reaches
the RDP Connected event without requesting credentials. It never receives
LoginComplete, and no logged-in child appears in `qwinsta`. Supplying the current
user/domain does not complete login either. The WTS API reports a reserved child
ID even before an interactive session exists; that ID is not evidence of readiness.
This setting has not been retained in Anode.

Anode previously launched its host on Connected. In this state Task Scheduler's
RunEx failed with 0x80070002 because the child had not logged in. Host launch now
waits for LoginComplete; recovering a host pipe also requires completed login.

## Alternative implemented

`anode start --sign-in` (or `anode up --sign-in`) enables the RDP control's native
credential dialog through IMsRdpClientNonScriptable5. The parent desktop stays
signed in. The Windows dialog accepts the account password; no password is passed
through Anode's CLI, pipes or logs. Credential saving is disabled. A new seat may
request credentials again. This is not a verified password-free solution.

The flag requires a visible viewer and a new daemon. When Anode is already running,
the CLI explains that it must be quit before changing the startup option. Quit
closes applications in the existing seat.

During this explicit mode, intermediate Windows authentication failures may lead
to another credential prompt. Anode waits for the native outcome instead of
treating such event-log entries as final. Unattended starts retain fast event-log
failure detection. Non-error OnLogonError notifications are distinguished from
terminal failures. Status exposes `loginComplete` and `signInPrompt`.

## Validation

- Debug build: zero warnings/errors; all 27 quick checks passed.
- Published Release via scripts/build.ps1 -QuickTest: all 27 checks passed.
- Real COM property readback covers all four native prompt properties.
- Native event tests prove Connected alone cannot start the host, LoginComplete
  can, automatic reconnect preserves completed login, and late events cannot
  restart cancelled startup. Interactive bad-password retry remains possible.
- `start --sign-in --hidden` is rejected before daemon launch.
- A published daemon launched through Task Scheduler displayed the Windows
  credential dialog. Its physical log path is shared with the CLI and logError
  is null. Parent console session 3 remained active.
- At 11:44, the published daemon was waiting for user credentials with
  agentReady=false, loginComplete=false and signInPrompt=true. Full authenticated
  seat startup, Liftoff and the pilot remain unverified.
- The disposable probe has exited. Its ignored source and logs are under
  artifacts/RdpProbe. No authentication dialog was operated by the agent.

References: Microsoft [OnLoginComplete](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onlogincomplete),
[OnLogonError notifications](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onlogonerror),
and [native credential prompting](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable5-allowpromptingforcredentials).

## User credential attempts rejected

The user reports entering the correct account password in both the disposable
probe and the published Anode dialog. A read-only elevated export at 11:47 found
Security event 4625 for the attempts at 11:45:02 and 11:45:08: MicrosoftAccount,
logon type 3, NtLmSsp/NTLM, status 0xC000006D, substatus 0xC000006A. Thus these
attempts reached Windows' local password verifier; they were not another failure
to start the listener or create its certificate. The ordinary RDP log only exposes
0x80004005 and reason 2055.

The current account's PrincipalSource is MicrosoftAccount. The local
DevicePasswordLessBuildVersion setting is 2 (Hello-only sign-in enabled). These
facts make local Microsoft-account password state a relevant hypothesis; they do
not prove that an online password is wrong or that refreshing its cache will fix
the seat. The export is kept locally in artifacts/signin-failures.json and no
passwords were collected. The elevated collector changed no machine settings.

Next diagnostic: use Windows **Run as different user** to launch the harmless
About Windows program under the same Microsoft account while the desktop stays
signed in. Success distinguishes local interactive acceptance from NTLM/RDP;
failure would show the problem is broader than Anode's RDP control. The actual
credential entry must be performed by the user. The viewer's misleading promise
that later seats will never prompt has also been removed.

The shell's `runasuser` verb could not be launched from this environment, so the
prepared check uses Windows' built-in `runas.exe` instead, in a visible console
titled **Anode - local Windows sign-in check**. It launches only winver.exe and
records the process exit code, never the password. The user has been asked to
report whether About Windows opens or an error appears. The failed Anode daemon
was stopped before this test; it again needed process termination after its Quit
acknowledgement. Debug and the rebuilt published executable both pass all 27
quick checks after the viewer-message correction.

## Subsequent retry and prompt cleanup

The user reported that the local test opened successfully. Its recorded runas
exit code was 1, and a narrowly timed audit query did not capture corroborating
success, so the reported result has not been independently confirmed. A new
unattended Anode attempt at 11:55:56 still failed with 0x80004005. Reopening the
explicit sign-in dialog did not resolve the issue; the user reported repeated
windows and continued inability to sign in. All test processes were stopped.
Do not reopen authentication prompts automatically for further investigation.

Additional fixes made without creating another live seat:

- Ordinary launches explicitly disable the RDP control's credential prompting.
  Only --sign-in enables it, and credential saving remains disabled.
- Stop posts viewer disconnection before attempting logoff, so a logoff error
  cannot leave the native authentication dialog connected.
- After a not-found logoff error, a successful WTS session enumeration that
  confirms the reserved child ID is absent makes cleanup a harmless no-op.
  Enumeration failure or a still-present session continues to surface errors.
- A guard refuses to log off session 0 or the parent session as a child.
- Regression checks use injected logoff/disconnect functions, with no live seat
  creation or input. They cover reserved IDs, uncertain failures, cancellation,
  and real COM settings that disable unattended credential prompts.

Windows authentication remains unresolved. No password reset, Hello policy
change, credential-store deletion, or additional live sign-in test was performed.
The final Debug build and published Release both passed all 28 quick checks.
Final status is Anode not running; no RdpProbe, runas or winver test process remains.
The console session remains active and RDP-Tcp remains listening.

## Fresh logon context experiment (failed before Anode launch)

A read-only query of the calling process's own token and LSA logon metadata at
15:30 found an Interactive (type 2), CloudAP logon in desktop session 3. This
describes the current context; it does not prove why child authentication fails.

Prepared an ignored diagnostic launcher under artifacts that uses Windows runas
to start Anode directly under a fresh same-account interactive logon, without
logging off the desktop. The helper checks the account SID, desktop session,
logon type, and changed logon ID before starting the daemon. It uses `anode up
--hidden` directly to inherit that logon context, avoiding a scheduled launch
that may select the existing desktop token. This is an unproven experiment,
not an established password-cache repair.

The launcher makes one attempt, uses no saved credentials, enables no RDP
credential dialogs, and records authentication and seat startup separately.
Failed startup stops only the daemon it launched. Guard validation passed without
authentication or creating a seat. No password is collected by the helper.
The user agreed to open the single Windows runas prompt. Launched it once at
15:45:57 on September 13 with a refreshed baseline and trial ID
8ca66e9f-a1d1-435b-bae5-b429cef7c918. Runas recorded exit code 1 at 15:52:13;
the authenticated helper never wrote its result. On September 14, the user
confirmed Windows reported an incorrect username or password. This test did not
authenticate a fresh process or launch Anode. There is no evidence this approach
repairs child-session authentication; do not reopen the same prompt automatically.

During preparation, `doctor` still incorrectly described the reserved child ID 5
as an existing, running seat. `qwinsta` showed only services, the active console,
and the RDP listener. Doctor now checks WTS session enumeration before describing
an existing child: a retained but absent ID is reported as no seat, enumeration
failure is a warning, and an existing session is not claimed to have completed
sign-in or agent startup. The rebuilt published executable correctly reports the
absent session on this machine. Debug build passed with zero warnings/errors;
Debug and published Release both passed all 28 quick checks. No authentication
window or live seat was opened during this verification.

The user requested a path without further Windows-password authentication and
agreed to investigate Windows Sandbox. Follow-up is recorded in
2026-09-14-password-free-sandbox-investigation.md. The existing child-session
authentication problem remains unresolved.

The user subsequently clarified that reusing installed applications and existing
accounts is essential. Sandbox has been shelved as an unsuitable substitute and
remains disabled; do not retry its administrator prompt. The target remains a
separate usable desktop under the existing Windows identity, without repeatedly
asking the user for the password. No working password-free replacement for the
current child-session path has been verified.

## Online password accepted; local identity mapping verified

On September 14, the user reports successfully signing into the Microsoft
website with the same password that Windows rejects. Attempting to set that
password again in Microsoft's reset flow was rejected as reuse of the old
password. An online password reset is not the next diagnostic step.

A read-only NetUserGetInfo level 24 query of the current Windows account returned
provider MicrosoftAccount and principal user@example.com, matching the
email supplied by the user and used in the earlier tests. The local account is
EXAMPLE-PC\example with the current user's SID. No email/account mismatch was found.
Only identity metadata was read; no password or credential material was queried.
The result is in ignored artifacts/windows-linked-account.json.

DevicePasswordLessBuildVersion remains 2, consistent with Hello-only device
sign-in. This and cached local credential state are possible contributors, not
proven causes. The local user's PasswordLastSet timestamp alone is not evidence
that its Microsoft-account credential cache is stale.

Suggested user-operated test: while online, allow password sign-in in Settings
without removing the PIN, then lock (not sign out) and unlock the existing
desktop using the Microsoft-account password. This preserves open applications
and distinguishes direct Windows password acceptance from the failed RDP/runas
attempts. The user can return to PIN sign-in if Windows rejects the password.
No setting was changed and no lock or credential prompt was opened by the agent.
The test has not yet been performed and is not claimed to repair authentication.

References: [linked identity metadata](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_24)
and [Windows sign-in options](https://support.microsoft.com/en-us/accounts-billing/security/sign-in-options-in-windows).

## Direct Windows password sign-in also fails with Hello-only disabled

The user disabled the Hello-only sign-in option and supplied a Settings screenshot
showing it Off. They report that direct Windows password sign-in still fails,
while the same Microsoft-account password works online. Disabling that setting
did not resolve the reported problem. Do not repeat the same reset, runas, or RDP
prompt as if it were an untried solution.

The user again requests a route without the Windows password. Requirements still
include their existing installed applications and signed-in accounts; Sandbox
does not meet those requirements and remains shelved. No working password-free
method for the isolated same-account child session has been verified. Operating
on the already signed-in main desktop could preserve application/account access,
but would share mouse, keyboard and focus with the user. That tradeoff requires
an explicit user decision before any main-desktop control; the existing Anode
seat host's refusal to operate in the parent session remains intact.

No account reset, account conversion, credential-store change, additional
authentication prompt, or desktop input was performed for this update.

## Retry after the user reset the password and signed in

At 18:12 on September 14, the user reported resetting the password and signing
in, and requested another separate background-seat attempt. Windows now reports
PasswordLastSet at 18:11:03 that day for the linked local account, changed from
the previous January 2024 value. The calling process still belongs to the same
CloudAP interactive logon ID 00000000:0FF70DE5 begun on September 13. These
timestamps do not by themselves establish what credentials RDP is using.

The published executable passed doctor and was launched through the shared
Task Scheduler path with --hidden and credential prompts disabled. The attempt
failed promptly with disconnect reason 2055: Windows could not sign the seat in.
Status preserved lastError, reported loginComplete=false and agentReady=false,
and recorded a valid logPath with logError=null. Session 5 remained reserved but
absent; console session 3 remained active and the RDP listener was listening.
Logs are under ignored artifacts/post-password-seat.

The failed daemon quit successfully. The user approved one explicit native
credential dialog to test the newly changed password. This attempt succeeded:
Windows completed sign-in and Anode reached ready at 18:14:12. Seat host PID 71868
ran as example in child session 5, distinct from parent console session 3.

With the viewer hidden, a 1280x720 screenshot succeeded and mouse movement was
confirmed by the child host's cursor report. A disposable PowerShell window was
launched in session 5, checked its session and account SID, and received the exact
test text through Anode's keyboard input. It recorded phase=passed and exited.
The main console stayed active; status showed loginComplete=true, agentReady=true,
lastError=null and logError=null. Artifacts are in post-password-interactive.

This validates a live separate seat after explicit sign-in with the new password.
It does not establish unattended sign-in for future fresh seats. The working seat
is being left running. Liftoff and the vision pilot have not yet been validated;
the Steam concurrency check is recorded in 2026-09-14-direct-steam-launch-disrupts-parent.md.
