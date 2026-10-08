# Guide: check an Android app on an emulator in the seat

Before a release, you want an agent to install your APK on an emulator, open it, look at a few
screens in light and dark mode, and check the log for crashes. Anode boots the emulator inside the
seat, so its window never appears on your screen, and its Android tools act only on that emulator,
never on your own emulators or a phone on USB.

![The same Android settings screen in light mode, then in dark mode after the agent switched it, captured from an emulator in the seat](https://raw.githubusercontent.com/skulitom/Anode/main/assets/guide-android-dark-theme.png)

<sub>Captured with Anode 0.11.0 from a Pixel 9 Pro emulator (API 36) in the seat: the device's own
pixels, before and after `cmd uimode night yes`. It booted in 6.7 s.</sub>

## Before you start

You need Android Studio's SDK and at least one AVD, as on your desktop. Check what Anode finds; this
starts nothing:

```powershell
anode android | Out-Host
```

Note the AVD name, such as `Pixel_9_Pro`. Emulators are heavy: run one at a time, and only while the
seat is free.

## Ask for the check

> Use Anode to boot Pixel_9_Pro in the seat, install C:\work\app-release.apk, open
> com.example.quiz, and send me screenshots of the first screen in light and dark mode. Then check
> logcat for crashes and stop the emulator.

Or run the guided prompt `/mcp__anode__android_test` and name your APK.

## What the agent does

```json
{"tool": "android_status", "arguments": {}}
{"tool": "seat_lease", "arguments": {"action": "acquire"}}
{"tool": "android_emulator", "arguments": {"action": "start", "avd": "Pixel_9_Pro"}}
{"tool": "android_emulator", "arguments": {"action": "adb", "serial": "emulator-5554", "args": ["install", "-r", "C:\\work\\app-release.apk"]}}
{"tool": "android_emulator", "arguments": {"action": "adb", "serial": "emulator-5554", "args": ["shell", "monkey", "-p", "com.example.quiz", "1"]}}
{"tool": "android_emulator", "arguments": {"action": "screenshot", "serial": "emulator-5554", "maxWidth": 540}}
{"tool": "android_emulator", "arguments": {"action": "adb", "serial": "emulator-5554", "args": ["shell", "cmd", "uimode", "night", "yes"]}}
{"tool": "android_emulator", "arguments": {"action": "screenshot", "serial": "emulator-5554", "maxWidth": 540}}
{"tool": "android_emulator", "arguments": {"action": "adb", "serial": "emulator-5554", "args": ["logcat", "-d", "-s", "AndroidRuntime:E"]}}
{"tool": "android_emulator", "arguments": {"action": "stop", "serial": "emulator-5554"}}
{"tool": "seat_lease", "arguments": {"action": "release"}}
```

- `start` returns the serial to use, such as `emulator-5554`, and `booted: true` once Android is up.
  It starts **read-only**: the AVD keeps nothing from the test, and it can run in the seat while the
  same AVD is open on your desktop.
- `screenshot` returns the device's own pixels. To tap something in it, scale the image's
  coordinates back to the device (`x * sourceWidth / width`) and use
  `["shell", "input", "tap", "X", "Y"]`.
- An empty `logcat -d -s AndroidRuntime:E` means no app hit an uncaught Java or Kotlin exception
  since boot. Native crashes log elsewhere (`logcat -d -s DEBUG:F` shows their tombstones).

## The same from a script

```powershell
$env:ANODE_AGENT_ID = 'android-check'
$lease = anode lease acquire | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Desktop acquisition failed.' }
$env:ANODE_LEASE_TOKEN = $lease.leaseToken
anode android start Pixel_9_Pro | Out-Host
anode android adb emulator-5554 -- install -r C:\work\app-release.apk | Out-Host
anode android adb emulator-5554 -- shell monkey -p com.example.quiz 1 | Out-Host
anode android shot emulator-5554 light.png | Out-Host
anode android adb emulator-5554 -- shell cmd uimode night yes | Out-Host
anode android shot emulator-5554 dark.png | Out-Host
anode android adb emulator-5554 -- logcat -d -s AndroidRuntime:E | Out-Host
anode android stop emulator-5554 | Out-Host
anode lease release | Out-Host
```

Use the serial `android start` prints; `android shot` saves the screen at the device's resolution.

## When it doesn't work

- **`booted: false`.** The boot outlasted the wait, and the emulator keeps booting; check
  `anode android` until the seat's emulator says `booted`. A cold boot takes longer.
- **adb keeps saying `device offline`.** The device itself is failing. After a quick boot, stop it
  and start again with `coldBoot` (`--cold`). If it still never comes online, the AVD may be damaged,
  which only you can repair or recreate; trying another AVD tells you quickly
  ([details](ANDROID.md#when-an-emulator-does-not-finish-booting)).
- **The screen stays black.** Start again with `gpu: "software"` (`--gpu software`)
  ([troubleshooting](TROUBLESHOOTING.md#an-android-emulator-will-not-start-or-stays-black)).
- **"runs outside the seat".** That serial is an emulator on your desktop, which Anode never drives;
  start one in the seat and use its serial.
- **The emulator keeps running after the test.** A lease can end between two steps, so emulators
  outlive it; stop yours when you're done.

More: [Android apps and web consoles](ANDROID.md).
