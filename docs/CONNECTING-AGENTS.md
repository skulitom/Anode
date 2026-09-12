# Connecting an agent

Anode speaks the Model Context Protocol on stdin and stdout. Any MCP client can hold a seat; this
page covers the two terminal agents most people use on Windows, plus the generic form.

The shape is the same for all of them. **The agent runs in your session and the seat is one of its
tools.** It does not run inside the seat, it does not touch your desktop, and it cannot take your
pointer. Everything it does goes through the daemon, which is also where your stop button lives, so
you and the agent are pointed at the same seat and you always win.

```
   your terminal                    your session                    the seat
  ┌──────────────┐   MCP stdio    ┌──────────────┐   named pipe   ┌────────────┐
  │ claude       │───────────────▶│ anode mcp    │───────────────▶│ programs   │
  │ codex        │                │      ↓       │                │ the agent  │
  └──────────────┘                │ anode daemon │                │ started    │
                                  │ + viewer     │                └────────────┘
                                  │ + STOP       │
                                  └──────────────┘
```

## 0. Put anode somewhere stable

The MCP client stores an absolute path, so pick one that will not move. Anywhere on your `PATH` is
convenient because you also get the bare `anode` command in a terminal.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
mkdir "$env:USERPROFILE\.local\bin" -Force
copy dist\anode.exe "$env:USERPROFILE\.local\bin\anode.exe"
```

Check the machine once, and do the one-time setup if you have not already:

```powershell
anode doctor
anode setup --fps 60 --gpu
```

Until `anode setup` has run, every tool call fails with a message naming the exact setting that is
missing, so an agent will tell you what to do rather than hanging.

## 1. Claude Code

```powershell
claude mcp add -s user anode -- "%USERPROFILE%\.local\bin\anode.exe" mcp
```

`-s user` makes the seat available in every project. Drop it for `local` (this project only, the
default), or use `-s project` to write a `.mcp.json` that you commit and your teammates get.

Check it:

```powershell
claude mcp list
claude mcp get anode
```

Inside a session, `/mcp` shows the server and its tools.

## 2. Codex CLI

```powershell
codex mcp add anode -- "%USERPROFILE%\.local\bin\anode.exe" mcp
```

That writes into `~/.codex/config.toml`:

```toml
[mcp_servers.anode]
command = 'C:\Users\you\.local\bin\anode.exe'
args = ["mcp"]
```

You can equally write those three lines by hand. Check it:

```powershell
codex mcp list
codex mcp get anode
codex mcp remove anode    # to undo
```

Codex ships its own computer-use tools, which drive **your** desktop. Anode is the complement: when
you want the model to click things without clicking your things, give it the seat instead.

## 3. Any other MCP client

```json
{
  "mcpServers": {
    "anode": {
      "command": "C:\\Users\\you\\.local\\bin\\anode.exe",
      "args": ["mcp"]
    }
  }
}
```

No environment variables, no arguments, no network. See [PROTOCOL.md](PROTOCOL.md) for the tool list
and for the named-pipe interface underneath, if you would rather skip MCP entirely.

## 4. Who starts the daemon

Either of you. It makes a small difference worth knowing about.

**You start it** (recommended when you are going to watch):

```powershell
anode start
```

The seat is up before the agent's first tool call, so the first call is fast, and the viewer is
already on screen.

**The agent starts it.** The first tool call brings the seat up on its own. Anode launches the daemon
through the Task Scheduler rather than as a child process, specifically because Claude Code and Codex
put their subprocesses in a job object: a daemon started the naive way would die with the agent and
strand a child session with no owner. As launched, the daemon outlives the agent, and closing your
terminal does not close the seat.

That means a seat can survive a session you have forgotten about. `anode status` finds it and
`anode kill` ends it, from any terminal.

## 5. Tell the agent how to use it

The tool descriptions carry a lot, but a few lines in your project instructions save a round trip.
Put this in `CLAUDE.md` for Claude Code, or `AGENTS.md` for Codex:

```markdown
## The Anode seat

The `anode` MCP server gives you a seat: a second Windows session with its own screen, pointer and
keyboard focus. Nothing you do there reaches the user's own desktop.

- Call `seat_status` first. If there is no seat, call `seat_start`.
- Use `seat_screenshot` with `maxWidth: 1000` and `format: "jpeg"` while waiting on something, and
  full size only when you need detail. Click coordinates always use the capture size, not the
  scaled size.
- Steam allows one instance per Windows user. Check `steam_status` before `steam_launch`. If Steam
  is already running outside the seat, say so instead of forcing it, because the game would open on
  the user's screen.
- `gamepad_set` is sticky: it changes only the fields you pass, so hold a stick and tap a button in
  two calls.
- Detach the gamepad when you are done. It is a machine-wide device the user's own games can see.
- Call `seat_stop` when the task is finished. Do not leave a seat running.
```

## 6. Check that it works

Ask for something small and verifiable:

> Bring up the Anode seat, open Notepad in it, type "hello", and show me a screenshot. Do not touch
> my desktop.

You should see the viewer window appear without stealing your focus, Notepad open inside it, and the
agent hand back an image. Your own screen should not have moved.

If it does not: `anode status`, then `anode doctor`, then the log at `%LOCALAPPDATA%\Anode\anode.log`,
which records every role including the one running inside the seat. More in
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## The other arrangement: the agent inside the seat

Everything above puts the agent in your session. You can also put the agent itself in the seat and
let it use its own computer-use tools there:

```powershell
anode start
anode run "C:\Program Files\WindowsApps\...\wt.exe"     # or cmd.exe, or your editor
```

Then, in that terminal inside the seat, run `claude` or `codex` normally. Their screen and input
tools now act on the seat's desktop, because they are processes in the seat, and the same isolation
applies for the same reason.

It is a real option and it costs you something: you lose the terminal in front of you, you drive the
agent through the viewer, and you no longer have a tool boundary between the agent and the seat's
whole desktop. Use the MCP arrangement unless you specifically want the agent's native computer-use
in an isolated session.
