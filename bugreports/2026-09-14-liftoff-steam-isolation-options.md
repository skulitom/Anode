# Liftoff while keeping Steam on the main desktop

Status: Sandboxie Plus 1.18.4 installed successfully after the user requested a
retry. The isolation probe passed, and a boxed Steam client reached its account
chooser in child session 5 while main-desktop Steam remained running in session 3.
The user completed Steam sign-in. Liftoff launched in the same box and rendered its
loading screen using the RTX 4090 / Direct3D 11. Menu loading stalled for several
minutes, then the log reported MainMenu complete. Hidden-viewer capture and game
controls are still unverified. Keep the existing
main-desktop Steam client running. Do not repeat a bare second-client launch.

## September 14 live trial

The authorized retry completed at 21:03 local time with installer and configuration
reload exit codes 0. SbieSvc and SbieDrv are running; no reboot was required.
The installer signature and SHA-256 were checked again before execution.

A disposable probe in child session 5 verified its account SID, loaded SbieDll,
and exact AnodeLiftoff membership through SbieApi_QueryProcess. It could not see
main-desktop Steam. Its marker and JSON output appeared only under the box's
virtualized file tree, with neither file present at the original host path.
See artifacts/liftoff-isolation-trial/installation-result.json and the boxed
isolation-result.json for the evidence. Helpers run in Windows PowerShell 5 and
must load SandboxieQuery.Framework.dll, not the .NET Core assembly.

Steam launched through Sandboxie's Start.exe /box:AnodeLiftoff in the verified
child seat. Main Steam PID 63080 (session 3, started 18:20:41) remained alive;
second Steam PID 66072 (session 5, started 21:07:28) reached its account chooser.
The Anode viewer was opened with control enabled for the user's native Steam
sign-in. No authentication tokens were extracted or copied. The resulting Steam
service installation warning was dismissed with Cancel; Steam continued without
reinstalling its shared service. The boxed process probe subsequently confirmed
Steam PID 66072 and Liftoff PID 4008 both belonged to AnodeLiftoff in session 5.
Main Steam PID 63080 remained running throughout these checks.

Sandboxie's controller added compatibility templates. A later configuration read
also showed global compatibility templates; their addition was not observed by
the agent. AutoRecover was reset to n; AutoDelete remains n. There are no force
rules or direct host-write grants in the trial section.

The trial box lacked the OpenBluetooth template. Sandboxie's official changelog
documents it as a default compatibility setting for Unity startup hangs. The
installed template narrowly configures Bluetooth RPC resolution and timeout.
After saving Sandboxie-before-bluetooth.ini, the agent appended this template to
AnodeLiftoff and reloaded successfully. Liftoff has not yet been restarted to test
the setting, so the slow first launch is not evidence that the change fixed it.

A separate capture issue appeared when the Anode viewer was minimized: screenshots
failed with "The handle is invalid" even though the child was active and ready.
Restoring the viewer recovered capture. `anode hide` initially allowed capture,
but subsequent captures failed too, so hiding alone is not a verified workaround.
This is separate from Steam authentication and Sandboxie installation. See
2026-09-14-hidden-viewer-capture-fails.md. The user explicitly rejected foreground
Computer Use because it disrupts their desktop. Do not repeat that fallback.

## Additional local evidence

Before the isolation trial, Steam PID 63080 remained in parent session 3 and
Anode's verified child host remained ready in session 5. No game was launched,
no controller was attached, and no new Windows sign-in was requested.

Opening the existing Local\Steam3Master_SharedMemFile read-only succeeded in
session 3; the equivalent Global\Steam3Master_SharedMemFile was absent. No mapping
contents were read or modified. Windows separates named kernel objects by session
unless the application creates them in the global namespace.

The SteamAPI_InitFlat probe was repeated with the process-local environment variable
steam_master_ipc_name_override=Steam3Master. In session 3 it returned code 0; in
session 5 it returned code 2, "Cannot create IPC pipe to Steam client process.
Steam is probably not running." This tested explicit client selection without
launching another client. It did not solve cross-session connectivity. Results
are in artifacts/liftoff-steam-probe/result-session-*-explicit-ipc.json and
master-namespace.json. This does not establish that every possible bridge is
impossible; building one would require more than a verified launch setting.

Sources: [Windows object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces),
[developer's original IPC-selection investigation](https://tsfreddie.com/blog/test-steam-networking/).

## Recommended next experiment: application isolation within the existing seat

Sandboxie Plus can launch installed applications inside an isolated environment.
The proposal is a persistent standard box called AnodeLiftoff, with Steam and its
Liftoff child process both boxed and both running in Anode's child session. The
existing Steam and game installation would be read through; writes would stay in
the box. No new Windows installation or Windows account is required for this
proposal. A separate native Steam sign-in may be needed; do not extract or copy
Steam authentication tokens. Main-desktop browsing/chat access is the requirement;
simultaneous game use through the same Steam account is a separate unverified limit.

This remains an experiment. The Sandboxie project has reports of successful Steam
use and also a June 2026 failure on Windows 11 25H2 with version 1.17.9. Liftoff on
this machine's current Steam build has not been validated in Sandboxie. Standard
isolation is the first test; do not automatically apply permissive IPC/window/file
exceptions just to make the client launch, since isolation is the point here.

Prepared under artifacts/liftoff-isolation-trial:

- Official Sandboxie-Plus-x64-v1.18.4.exe, release dated September 6, 2026.
- SHA-256 cfceda1b1a63abcd2b3e5fbfdc4895f15db56316f5a9f2f06aea230e209b120e,
  matched the GitHub release asset digest. Authenticode status Valid, signer
  NOVASYNC LABS PTE. LTD.; details in installer-verification.json.
- AnodeLiftoff.ini is the trial section template, not a complete replacement for
  Sandboxie.ini. It uses persistent per-box storage under the user's LocalAppData,
  hides host Steam processes, and contains no global force rules or host-write grants.

Installing Sandboxie adds a system driver and service and requires elevation.
The user explicitly approved installation and testing. The first elevation
returned error 1223 before the script started. The user then said "try again,
i'm back"; the second elevation succeeded and the script completed configuration.

The elevated script verifies the installer again, installs without
an automatic reboot or optional tasks, backs up an existing Sandboxie.ini, appends
only AnodeLiftoff, and reloads configuration. The isolation probe checks the child's
SID/session, loaded Sandboxie module, and exact box membership through the
documented native API before writing a disposable marker. Host visibility and
copy-on-write behavior are checked before any second Steam client is launched.

Trial checklist (steps 1-4 passed; step 5 rendered loading screen; step 6 pending):

1. Preserve any existing Sandboxie configuration. Add only the named trial box.
2. Run a disposable boxed probe via Anode. Verify it is in the current verified
   child session, writes remain in the box, and the host Steam process is hidden.
3. Launch D:\STEAM\steam.exe through Sandboxie's Start.exe /box:AnodeLiftoff
   from Anode, and verify actual child-session and box membership. Do not use force
   on Anode's regular Steam launcher. Watch the original parent Steam process.
4. If Steam needs authentication, have the user sign into Steam's own dialog.
5. Launch installed app 410340 from that boxed client. Verify Liftoff's process,
   rendering and capture in the seat before attempting input.
6. Test controls separately: Anode's virtual gamepad is machine-wide, so successful
   Steam isolation alone does not establish controller isolation. Avoid parent-game
   interference. Only then attempt the requested vision pilot.

If the trial fails, terminate only the trial box and preserve its diagnostic logs.
The installer can be removed normally if the experiment is abandoned. Do not
automatically restart Steam, log off the working seat, or delete game/save data.

Sources: [Sandboxie project](https://github.com/sandboxie-plus/Sandboxie),
[release 1.18.4](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.4),
[documented launcher](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine.html),
[hide host process setting](https://sandboxie-plus.github.io/sandboxie-docs/Content/HideHostProcess.html),
[project compatibility reports](https://github.com/sandboxie-plus/Sandboxie/discussions/4172),
[25H2 Steam report](https://github.com/sandboxie-plus/Sandboxie/issues/5438).

## Other routes considered

| Route | Fit for this request |
| --- | --- |
| DuoStream Steam isolation | Purpose-built multiseat option. Its current documentation explicitly describes process patches to prevent a new Steam instance terminating existing ones. Requires a larger system integration; not installed. |
| Dedicated VM / Windows Sandbox | Separate Steam runtime, but needs a separate environment and graphics/controller validation; the earlier Windows Sandbox detour remains shelved at the user's request. |
| Steam Remote Play alone | Streams a game running on the host; it does not by itself create the independent background execution session needed here. |
| Offline mode or launching Liftoff.exe directly | Offline mode still uses Steam. No supported Steam-free launch for the installed Liftoff build was established. Do not replace game DLLs or license checks. |

DuoStream's documentation is stronger evidence than blanket claims that two
Steam clients can never run on one machine. The unresolved issue is how to isolate
them reliably here while preserving the user's current access. A name override
alone has known regressions and did not fix this machine's cross-session probe.

Sources: [Duo isolation settings](https://github.com/DuoStream/Duo/wiki/Settings),
[Duo release notes](https://github.com/DuoStream/Duo/releases),
[Steam Remote Play](https://help.steampowered.com/en/faqs/view/0689-74B8-92AC-10F2),
[Steam offline mode](https://help.steampowered.com/en/faqs/view/0E18-319B-E34B-B2C8),
[Liftoff developer statement](https://steamcommunity.com/app/410340/discussions/0/1693785669870791405/?ctp=3),
[current Liftoff support](https://www.liftoff-game.com/support?category=1&post=44&topic=3).
