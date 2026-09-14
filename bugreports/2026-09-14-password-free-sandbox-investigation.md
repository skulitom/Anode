# Investigating a Sandbox desktop without the host-account password

## Current decision: shelved because it does not meet the user's goal

On September 14 the user clarified that Anode must reuse their installed
applications and existing accounts. A new Sandbox environment does not preserve
that experience, so this is not an equivalent repair. Do not reopen the enablement
prompt or continue this experiment on the basis of the earlier approval.
Sandbox remains disabled. Preserve the existing user profile and independent
input requirement when evaluating further solutions.

Microsoft's Power Automate documentation also distinguishes a same-session
virtual-desktop mode from child sessions: its virtual-desktop mode does not
support physical mouse/keyboard input, screenshots or image-based actions. It
therefore does not establish an equivalent solution for Anode's vision-driven
Liftoff use case. Browser profile and single-instance application restrictions
also mean that a same-account child session cannot promise unrestricted reuse
of every already-running application.

Reference: [Power Automate PiP modes and limitations](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip).

## Earlier investigation

The user reports that both RDP credential prompting and the one-time Windows
runas test reject the supplied account credentials. Runas exited 1 and no
authenticated-process marker exists. Stop repeating the password prompts.

Anode's current backend depends on Windows completing child-session sign-in.
Microsoft documents automatic logon from an existing session, rather than an
anonymous child-session option. Earlier disabling-delegation probes connected
without completing logon, so they did not establish a usable seat.

The user agreed to investigate Windows Sandbox as a different backend. Sandbox
provides a separate disposable Windows environment with its own container user,
avoiding the host Microsoft-account credential path. This is an experiment, not
an implemented Anode backend or a verified Liftoff solution.

## Machine and prepared check

- Windows 11 Pro 25H2; hypervisor present; approximately 64 GB RAM.
- Containers-DisposableClientVM was disabled (Win32_OptionalFeature state 2).
- No live child seat or Anode process was found; parent console remains active.
- Ignored artifacts/SandboxProbe contains a .wsb configuration and bootstrap
  script that records guest account, session, machine and graphics metadata.
- Networking, clipboard, microphone, camera and printer sharing are disabled.
  Only the probe input folder is mapped read-only and a dedicated probe-output
  folder is writable. No account profile or Steam library is mapped.
- The probe performs no screen capture, input injection, or Anode seat launch.
  XML, folder references and PowerShell syntax were checked locally.
- The approved enablement script uses Enable-WindowsOptionalFeature with
  -NoRestart and records the outcome. The elevation launch returned Windows'
  "operation was canceled by the user" error before starting the script. No
  enablement result exists, and the feature remains disabled. The prompt was not
  reopened automatically. A required reboot would be reported, not initiated.

## Remaining work and limits

First verify that Sandbox starts without asking for the host-account password
and that its guest bootstrap runs. Then evaluate a guest control transport and
identity verification before enabling any input. Existing child-session host
guards remain unchanged. Anode's local named pipes do not automatically cross
into a Sandbox VM.

Sandbox starts clean and discards guest state on close. Host-installed programs
are not automatically available. Steam/Liftoff setup and possible Steam sign-in,
graphics performance, controller support, and persistence require separate
validation. A successful environment probe is not a working Anode seat.

References: [child sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions),
[Sandbox overview](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/),
[installation](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install),
[configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file),
and [CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli).
