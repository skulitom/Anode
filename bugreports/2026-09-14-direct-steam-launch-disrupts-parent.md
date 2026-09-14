# Direct Steam launch through run can disrupt the parent client

After the child seat became usable on September 14, the user asked whether Steam
could also run in the seat while remaining accessible on their main desktop.
Steam PID 31720 was initially in parent session 3.

A single `anode run D:\STEAM\steam.exe -silent` launched PID 76260 in child
session 5. Both PIDs briefly appeared in process enumeration. Both then exited;
Steam's bootstrap log recorded Shutdown at 18:18:41. The user explicitly reported
that Steam closed on their side and moved, and confirmed they had not closed it
manually. No shutdown command was issued during that test. This did not validate
two independently usable clients and disrupted the user's desired main-desktop
access. Do not repeat the experiment.

Steam was restored by launching it from the parent session. PID 63080 was verified
in session 3. The Anode daemon and host remained ready in sessions 3 and 5,
respectively. No Liftoff game or virtual gamepad was started.

## Fix

`steam.launch` already refused a conflicting client, but the generic `run`
operation allowed the same direct Steam launcher. The run path now checks direct
Steam commands, executable paths, file URLs, and Steam URLs before Process.Start,
and returns a clear error if Steam is running outside the seat. The check covers
direct launches, not the contents of shortcuts or wrapper scripts.

The MCP description and README no longer promise that every application window
will stay in the seat: applications may reuse an existing process across
sessions. The seat identity guard and session-local input/capture remain intact.

Regression checks exercise the actual run handler with an injected process
launcher and session inventory. Conflicting commands and URLs must never call
the launcher; same-seat/absent Steam and ordinary programs retain their behavior.
No Steam client or actual seat is created by those checks.

Debug build passed with zero warnings/errors and all 29 quick checks passed.
Published Release under artifacts/steam-guard-build also passed all 29 quick
checks. This separate output keeps the working seat alive. The running dist executable has
not been replaced or restarted; the guard takes effect with the updated host.

## Can the child game connect to the existing parent client?

A disposable PowerShell probe loaded Liftoff's installed steam_api64.dll and called
SteamAPI_InitFlat with app ID 410340 supplied through a workspace steam_appid.txt.
It did not call RestartAppIfNecessary, start the game, or launch another client.
The probe checked its expected session and user SID before running, then called
SteamAPI_Shutdown only after successful initialization. No game files were modified.

In child session 5, initialization returned code 2 with the message:
"Cannot create IPC pipe to Steam client process.  Steam is probably not running."
The same probe under the same user in parent session 3 returned code 0 (success).
Steam PID 63080 stayed in session 3 throughout; RunningAppID was 0 afterward.
This comparison establishes that the tested cross-session connection does not
work here, rather than indicating an absent client or a wrong API invocation.
The first probe used the unexported SteamAPI_Init entry point; that failed invocation
is not counted as connectivity evidence. Results and the corrected probe are in
ignored artifacts/liftoff-steam-probe.

Do not move Steam again: the user wants continued access on the main desktop.
Anode's status and troubleshooting now explain this tradeoff without presenting
closing the user's client as the required next step. The working child seat stays
available for compatible applications. Liftoff and the vision pilot remain untested
inside it because no working connection to the parent Steam client was found.
