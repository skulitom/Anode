# Giving an agent a seat

A worked example: Claude Code launches a Steam game in the seat, plays it with a virtual controller,
and hands the seat back. You watch the whole thing in a window and can stop it at any moment.

## 1. Register the MCP server

**Claude Code**

```powershell
claude mcp add anode -- "C:\Tools\anode\anode.exe" mcp
```

**Claude Desktop**, or any other MCP client: add [`mcp-config.json`](mcp-config.json) to your
`mcpServers`, with the path corrected to wherever you put `anode.exe`.

Nothing else is needed. The MCP server starts the daemon itself on the first tool call, and the
viewer stays hidden. Use `seat_show` only when the user wants to watch.

## 2. Check the machine once

```powershell
anode doctor
anode setup --fps 60 --gpu   # one administrator prompt; the last two flags matter for games
```

Reboot if it asks. The frame-rate and GPU settings only take effect after one.

## 3. Ask for something

> Bring up a seat, launch Half-Life 2 from Steam in it, wait for the main menu, and show me a
> screenshot. Don't touch my desktop.

A reasonable tool sequence:

```
seat_status                     -> state: stopped
seat_start                      -> session 3, ready
steam_status                    -> "Steam is not running. Starting a game from the seat will start
                                    Steam in the seat, and the game opens in the seat."
steam_launch    {appId: 220}    -> launching
seat_screenshot {maxWidth: 1000, format: "jpeg"}   -> still loading
seat_screenshot {maxWidth: 1000, format: "jpeg"}   -> main menu
```

Then, to play it:

```
gamepad_attach  {}
gamepad_tap     {button: "a"}
gamepad_set     {axes: {lx: 0.7, ly: 0.0}}     hold the stick right
gamepad_set     {axes: {lx: 0.0, ly: 0.0}}     let go
seat_key        {keys: "esc"}
```

`gamepad_set` is sticky: it changes only the fields you pass, so holding a stick while tapping a
button is two calls.

## 4. Watch it, and stop it

The viewer window shows the seat live. It starts in **view only**, so a stray click of yours does not
land in the agent's game. Press **Take control** to drive it yourself, and **Release control** to hand
it back.

Stopping always works, even if the game has hung:

- `Ctrl+Alt+Shift+K` from anywhere
- the **Stop seat** button, or the tray icon
- `anode kill` in a terminal
- asking the agent to call `seat_stop`

All four sign the child session out, which force-closes everything running in it.

## Things worth telling the agent

Put these in your project instructions or `CLAUDE.md` if you use a seat often:

```markdown
## The Anode seat

`anode` gives you a seat: a second Windows session with its own screen, pointer and focus. Your
input never reaches the user's own desktop.

- Call `seat_status` first. If there is no seat, `seat_start`.
- `seat_screenshot` with `maxWidth: 1000` and `format: "jpeg"` while polling; full size only when
  you need detail. Click coordinates always use the capture size, not the scaled size.
- Steam is one instance per Windows user. Check `steam_status` before `steam_launch`; if Steam is
  already running outside the seat, say so rather than forcing it, because the game would open on
  the user's screen.
- Detach the gamepad when you are done with it. It is a machine-wide device.
- Call `seat_stop` when the task is finished. Do not leave a seat running.
```

## If something goes wrong

`anode status`, then `%LOCALAPPDATA%\Anode\anode.log`, then
[docs/TROUBLESHOOTING.md](../../docs/TROUBLESHOOTING.md).
