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

## What's new in 0.10.0

**Play your own games while agents test games in the seat.** A virtual controller is a device on
the machine, so a game on your desktop read the input an agent sent to a game in the seat. With
[HidHide](https://github.com/nefarius/HidHide/releases) installed, Anode keeps every virtual
controller plugged in while the seat runs inside the seat: games, Steam and the Xbox Game Bar on your
desktop cannot open it, and games in the seat use it as before. That includes controllers a program
in the seat plugs in on its own. While a ViGEm program such as DS4Windows runs on your desktop, only
Anode's own controllers are kept in the seat, so yours keep working. After plugging a controller in,
Anode checks from your desktop that it cannot be opened there, and unplugs it again (`not_isolated`)
rather than let your desktop read it. `gamepad_attach` reports `seatOnly`, `anode gamepad state`
shows every controller, and `anode doctor` checks for HidHide. Without HidHide, controllers are
machine-wide as before, and each attach says so.

**The viewer opens fitted to the seat.** The viewer measures its frame and bars and opens with the
seat at its own size, or the largest that fits your screen, instead of scaling it slightly with white
strips at its sides. A resized, maximized or full-screen viewer keeps the seat's shape on its dark
background.

**Upgrading.** Save seat work, run `anode quit` (it closes every program in the seat) and close the
agent sessions that use Anode, then install 0.10.0 and start Anode again. To keep controllers in the
seat, install HidHide from its [releases](https://github.com/nefarius/HidHide/releases) and restart
Windows when it asks; `anode doctor` then reports **Controller isolation**. Anode changes only its own
HidHide entries and switches HidHide on only when nothing else is listed. Run `anode configure` to
update the installed skill.

**Validation and limits.** The candidate passes all 61 local quick checks, including new checks of
HidHide's lists, jail entries, reserved controller names, your own HidHide entries and settings, ended
seats, ViGEm programs outside the seat, controller attach against stand-ins and the fitted viewer,
plus package, installation and distribution checks. Live, with HidHide 1.5.230, Anode's controller
and one plugged in by a program in the seat were in XInput slot 0 in the seat, while the desktop saw
no controller and was refused both of its devices; stopping the seat removed its HidHide entries; and
the viewer opened with the 1280x720 seat filling it. HidHide checks each attempt to open a device, so
a device name Windows has never used before is hidden once Windows announces it, and a desktop
program that opens HID devices the moment they arrive could open that one first; XInput is always
covered. Programs on HidHide's application list can open hidden devices anywhere. The browser suite
with real input on .NET 10 has still not run. See the
[validation record](https://github.com/skulitom/Anode/blob/main/docs/RELEASE-READINESS.md#live-validation--25-september-2026).

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
