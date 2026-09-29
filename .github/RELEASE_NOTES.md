## Install Anode on Windows

Download **anode-windows-x64.zip** below for the portable executable, agent connector and docs.
Release builds include .NET; no SDK or runtime installation is needed.

For a per-user installation, download **install.ps1** and run it in PowerShell. It adds Anode to
your PATH, the Start menu and **Settings → Apps → Installed apps**:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Open a new terminal, then:

```powershell
anode doctor | Out-Host
anode setup | Out-Host
anode configure | Out-Host
anode start --hidden | Out-Host
anode capabilities | Out-Host
```

`configure` finds installed Codex and Claude Code CLIs, backs up their settings, and installs
the discoverable `anode-desktop` skill. Use `--no-skill` for MCP registration alone.
Restart the agent afterward. Other MCP clients can launch `anode.exe` with the argument `mcp`.

## What's new in 0.11.0

**Test Android apps in the seat.** `android_emulator` (`anode android start`) boots an AVD on an
emulator inside the seat, so its window never opens on your screen, and returns its serial once it
has booted. Agents install and drive apps with adb for that serial, take the device's own
screenshots and stop it when done. Emulators start read-only: what a test does is dropped when the
emulator stops, and the AVD can run in the seat while you have it open on your desktop. Every Android
action checks that its emulator runs in the seat, so your own emulators and phones, which share the
adb server, stay out of reach. `android_status` (`anode android`) lists the SDK, AVDs, running
emulators and Android Studio without starting anything, and the `android_test` prompt walks a release
test.

**Android Studio and signed-in web consoles.** `android_studio` opens Android Studio in the seat on a
profile of its own, with its setup wizard skipped, so it never hands projects to a Studio on your
desktop. `seat_browser` opens Chrome or Edge in the seat on a persistent seat profile. Sign in once
from your own terminal with `anode browser --sign-in https://play.google.com/console`, and agents find
the sites signed in. Anyone holding the desktop lease can use those sites as you, so sign in only to
what agents should act on.

**Test other displays.** `seat_display` (`anode display`) changes the running seat's resolution and
Windows scaling, from 640x480 to 8192x8192 at 100-500%, portrait included, while its apps keep
running. A change lasts while the agent holds the desktop lease; the display the seat started with
comes back when the lease ends. The `display_test` prompt runs an app through a set of displays and
asks what breaks at each. Screenshots, input and control bounds now use physical pixels at every
scaling.

**Agent audio.** Agents can listen to the seat's audio as WAV clips and play WAV files in it, in a
seat started with `--audio`, which is also audible on your speakers.

**Upgrading.** Save seat work, run `anode quit` (it closes every program in the seat) and close the
agent sessions that use Anode, then install 0.11.0 and start Anode again. Restart agent sessions to
get the new tools, and run `anode configure` to update the installed skill.

**Validation and limits.** The candidate passes all 78 local quick checks, which drive the emulator,
Studio and browser code with stand-ins for every process, plus package, installation and distribution
checks. Live, on Windows 11 Pro build 26200 with emulator 36.2.12, a Pixel 9 Pro AVD booted in the
seat in 12.2 s on the graphics card, adb reported its 1280x2856 screen, a screenshot came back at that
size and it shut down cleanly; Android Studio opened at its Welcome screen on its own profile; Chrome
opened on the seat profile, and `run` refused Chrome and links while Chrome ran on the desktop; and
every display change, up to 2560x1440 at 125%, 1920x1080 at 150% and 1080x1920 portrait, applied live
in 156-173 ms. Audio has not been tested live, and the display change's reconnect fallback has not run
live. Known issues, with fixes awaiting review: `android_emulator`'s adb guard lets some server
commands through, such as one after a `wait-for-device` prefix; `seat_audio_listen` fails for MCP
clients on protocol 2024-11-05; and a display change Windows did not apply can come back when the
viewer reconnects. See the
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#live-validation--28-29-september-2026).

Requires 64-bit Windows 10/11 Pro, Enterprise or Education, or Windows Server with a Remote Desktop
host. Windows Home is unsupported. ARM64 is not validated; this package targets x64.
`setup` asks for administrator permission to enable Remote Desktop and child sessions.
It does not add firewall rules; it can restart Remote Desktop Services.

The installer verifies **SHA256SUMS** before installing the archive. Binaries and scripts are
currently unsigned; Windows may display a SmartScreen warning. Checksums detect corruption and do
not establish publisher identity. Review the source and your organization's policy before running.

See [installation, updates and removal](https://github.com/skulitom/Anode/blob/main/docs/INSTALL.md),
[agent configuration](https://github.com/skulitom/Anode/blob/main/docs/CONNECTING-AGENTS.md),
and the [changelog](https://github.com/skulitom/Anode/blob/main/CHANGELOG.md).
