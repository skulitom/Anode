# Giving an agent a seat

A worked example: Claude Code launches a Steam game in the seat, plays it with a virtual controller,
and hands the seat back. You can watch the whole thing in a window and stop it at any moment.

## 1. Register the MCP server

**Claude Code**

```powershell
anode configure claude | Out-Host
```

For manual registration see [Connecting agents](../../docs/CONNECTING-AGENTS.md#1-claude-code).

**Claude Desktop**, or another client with an `mcpServers` config: add
[`mcp-config.json`](mcp-config.json) to your `mcpServers`, with the path corrected to wherever you
put `anode.exe`. See [Claude Desktop](../../docs/CONNECTING-AGENTS.md#claude-desktop), and
[VS Code](../../docs/CONNECTING-AGENTS.md#vs-code) for its `servers` form.

Once machine setup is complete, `seat_lease` with `action: "acquire"` (or `seat_start`) starts a
hidden daemon and seat; `anode_guide`, `seat_status` and lease status never do. Use `seat_show`
only when the user wants to watch.

## 2. Check the machine once

```powershell
anode doctor | Out-Host
anode setup --fps 60 --gpu | Out-Host   # one administrator prompt; the last two flags matter for games
```

Reboot if it asks. The frame-rate and GPU settings only take effect after one.

## 3. Ask for something

> Bring up a seat, launch Half-Life 2 from Steam in it, wait for the main menu, and show me a
> screenshot. Don't touch my desktop.

A reasonable tool sequence:

```
seat_status                                          -> state: stopped
seat_lease      {action: "acquire", ttlSeconds: 600} -> starts a hidden seat; MCP keeps the token
seat_capabilities                                    -> capture and input available
steam_status                                         -> "Steam is running on your desktop (session 1). ..."
steam_launch    {appId: 220}                         -> hl2.exe runs in the seat; Steam stays on the desktop
seat_screenshot {maxWidth: 1000, format: "jpeg"}     -> still loading
seat_lease      {action: "renew", ttlSeconds: 600}
seat_screenshot {maxWidth: 1000, format: "jpeg"}     -> main menu
```

Then, to play it:

```
gamepad_attach  {}
gamepad_tap     {button: "a"}
gamepad_set     {axes: {lx: 0.7, ly: 0.0}}     hold the stick right
gamepad_set     {axes: {lx: 0.0, ly: 0.0}}     let go
seat_key        {keys: "esc"}
gamepad_detach  {}
seat_lease      {action: "release"}
```

`gamepad_set` is sticky: it changes only the fields you pass, so holding a stick while tapping a
button is two calls. Renew the lease during long play; release or expiry also detaches the
controller and releases held input.

## 4. Watch it, and stop it

Run `anode show | Out-Host` (or use the tray icon) to watch; a seat the agent starts stays hidden.
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

If you use a seat often, put the maintained snippet from
[Connecting agents](../../docs/CONNECTING-AGENTS.md#5-tell-the-agent-how-to-use-it) in your
project instructions or `CLAUDE.md`.

## If something goes wrong

```powershell
anode status | Out-Host
```

Then read `%LOCALAPPDATA%\Anode\anode.log` and
[docs/TROUBLESHOOTING.md](../../docs/TROUBLESHOOTING.md).
