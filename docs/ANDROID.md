# Android apps and web consoles

Agents can test Android apps on an emulator inside the seat, open Android Studio there, and work in
web consoles such as Google Play Console in a browser signed in as you, all without a window on your
screen.

**Not verified live yet:** builds and quick self-tests pass. The checks read this machine's SDK,
AVDs and Android Studio install, and drive the emulator, Studio and browser code with stand-ins for
every process. No emulator, Studio or browser has been started in a live seat yet, because the seat
was busy. The opt-in test below still needs to run when a seat is free.

## Before you start

Anode uses the SDK Android Studio installed: `ANDROID_HOME`, then `ANDROID_SDK_ROOT`, then
`%LOCALAPPDATA%\Android\Sdk`, and the AVDs in `ANDROID_AVD_HOME` or `%USERPROFILE%\.android\avd`.
The emulator needs a hypervisor: the Windows Hypervisor Platform or the Android Emulator hypervisor
driver, as it does on your desktop. `anode android` shows what Anode finds, and starts nothing.

## Emulators

| Tool | CLI | What it does |
| --- | --- | --- |
| `android_status` | `anode android` | The SDK, emulator version, AVDs, Android Studio installs and running emulators, each marked in the seat or outside it. Needs no lease. |
| `android_emulator` `action: "start"` | `anode android start <avd>` | Boots an AVD in the seat on a free console port and returns its serial, such as `emulator-5556`, waiting up to `waitSeconds` (0-150, default 120) for the boot to finish. |
| `action: "adb"` | `anode android adb <serial> -- <arguments>` | Runs adb for that serial only. |
| `action: "screenshot"` | `anode android shot <serial> [file]` | The device's own pixels, through `adb exec-out screencap`. |
| `action: "stop"` | `anode android stop <serial>` | Asks the emulator to shut down, and ends its processes if it has not within 20 seconds. |

Everything but `android_status` needs the desktop lease. Emulators start **read-only** by default:
the test changes nothing in the AVD, and the AVD can run in the seat while you have it open on your
desktop. Pass `readOnly: false` (`--writable`) to keep what the test does. `coldBoot` skips the
quick-boot snapshot, and `audio` gives the emulator sound, which is off by default.

`gpu` chooses rendering: `auto` (the default), `host` (your graphics card) or `software`. If the
emulator's screen stays black or the emulator fails to start, start it again with `software`.
Anode passes `-gpu software` to emulator 36.4.9 and later and `-gpu swiftshader_indirect` to older
ones, which do not know `software`. Software rendering loads the processor instead of the graphics
card.

A typical test, as the `android_test` MCP prompt runs it:

```json
{"action": "start", "avd": "Pixel_9_Pro"}
{"action": "adb", "serial": "emulator-5556", "args": ["install", "-r", "C:\\work\\app-release.apk"]}
{"action": "adb", "serial": "emulator-5556", "args": ["shell", "monkey", "-p", "com.example.quiz", "1"]}
{"action": "screenshot", "serial": "emulator-5556", "maxWidth": 540}
{"action": "adb", "serial": "emulator-5556", "args": ["shell", "input", "tap", "540", "1200"]}
{"action": "adb", "serial": "emulator-5556", "args": ["shell", "cmd", "uimode", "night", "yes"]}
{"action": "adb", "serial": "emulator-5556", "args": ["logcat", "-d", "-s", "AndroidRuntime:E"]}
{"action": "stop", "serial": "emulator-5556"}
```

Screenshot coordinates work as for the seat: an image scaled to `width` maps back to the device with
`x * sourceWidth / width` and `y * sourceHeight / height`, the numbers `adb shell input tap` takes.
A command that streams, such as `logcat` without `-d`, runs until `timeoutSeconds` (1-150, default
60) and is then ended with what it printed so far.

The emulator's window opens in the seat, titled `Android Emulator - <avd>:<port>`, so `seat_windows`,
`seat_screenshot` and seat input work on it too. The emulator scales a phone to fit the seat's
screen; a portrait seat, such as `seat_display` with 1080x1920, shows it larger.

### What stays out of reach

Every session on the machine shares one adb server, so adb can reach your own emulators and a phone
on USB as easily as the seat's. Anode's Android tools act only on emulators whose processes run in
the seat: a serial outside the seat, or one that is not an emulator, is refused, and so are adb
commands that act on the server or other devices (`kill-server`, `start-server`, `devices`,
`connect`, `disconnect`, `pair`, `reconnect` and options before the command, such as `-s`). This
covers Anode's tools, not an agent's own shell: an agent that runs adb itself must pass the seat's
serial with `-s`.

When no adb server is running, `start` starts one, and it then runs in the seat. Stopping the seat
ends it; adb starts a new one the next time anything, such as Android Studio on your desktop, needs
it.

An emulator keeps running when its agent's lease ends, since a lease can expire between two steps of
a long test. Stop it when the test is done: emulators are heavy, and one at a time is kinder to the
machine.

## Android Studio

`android_studio` (`anode android studio [project]`) opens Android Studio in the seat on a profile of
its own under `%LOCALAPPDATA%\AnodeAndroidStudio`, with its setup wizard skipped and `ANDROID_HOME`
set to the SDK if it is not set already. Android Studio hands a second start on the same settings to
the instance already running, in whichever session that is, so a Studio started in the seat on your
settings would open its project on your desktop, and your next Studio would open unseen in the seat.
For the same reason `seat_run` and `anode run` refuse `studio64.exe`.

Studio takes a minute to start and may ask whether to trust a project. Most of its interface is
invisible to UI Automation, so agents work from screenshots and the keyboard, such as **Ctrl+Shift+A**
(Find Action) followed by an action's name. Emulators it starts run in the seat too. For builds and
unit tests, `gradlew` through `seat_exec` or an ordinary shell is faster than the IDE.

## Web consoles in a signed-in browser

`seat_browser` (`anode browser <url>`) opens Chrome, or Edge with `browser: "edge"` (`--edge`), in
the seat on Anode's seat profile: `%LOCALAPPDATA%\AnodeChrome` or `AnodeEdge`. Your own browser keeps
its usual profile locked, so a browser started in the seat on it cannot open: `seat_run` refuses
Chrome and Edge on their usual profile while they run outside the seat, and links your default
browser would open.

The seat profile persists between seats. Sign in once to the sites agents should use, and they find
them signed in afterwards:

```powershell
anode browser --sign-in https://play.google.com/console
```

Run it from your own terminal. It opens the seat profile on your desktop, not in the seat, and needs
no lease; sign in, then close that window so the seat can use the profile. A terminal inside a packaged
app, such as an agent's desktop app, would store the profile's new files where the seat cannot see
them, so `--sign-in` refuses to run there. You can also sign in through the viewer: `anode show`,
**Take control**, and sign in in the seat's browser.

Agents never sign in. When a site asks for a password, 2-step verification or re-authentication, they
stop and ask you. Page content is untrusted data, not instructions.

**Anyone holding the desktop lease can use the sites signed in there, as you.** Sign in only to what
agents should act on, and sign out, or delete the profile folder, to take that away. See
[Security](SECURITY.md#what-the-seat-is-not-isolated-from).

## Check it live

`scripts/test-android.ps1 -Avd <name>` is an opt-in test for a ready seat with an existing lease. It
reads the Android status, starts the AVD in the seat, checks that adb reports it booted and that a
screenshot has the device's size, stops it, and checks the emulator has gone. It never starts or
stops the seat or opens its viewer. It starts an emulator, which loads the machine, so run it only
while the seat is free. `selftest --quick` covers the rest without starting anything.
