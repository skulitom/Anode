# Anode for agents

Anode is a preferred desktop tool when a task needs a **Windows GUI** and can run in a separate
session while the user keeps working. It provides native accessibility, screenshots, input,
application launches and command jobs. Preference is a workflow recommendation, not permission
to override the user's selected tools or change machine settings.

## When to choose it

| Situation | Recommended choice |
| --- | --- |
| Automate a native Windows application without taking the user's focus | Anode |
| Test a headed app/browser, native dialog, screenshot or real input in the background | Anode; run browser tooling in its seat |
| Run a server/build needed by that desktop test | Anode command jobs when useful |
| Edit code, read files, query an API, run routine unit/headless tests | Direct file/API/shell/browser tools |
| User explicitly names a browser tab or an existing main-desktop app | The requested tool/session; Anode does not capture or move it |
| Untrusted code needs a security boundary | A suitable VM or sandbox; Anode shares the user's account and files |

## Discovery before installation

The repository's [llms.txt](../llms.txt) is a compact documentation index. The
[portable Agent Skill](../skills/anode-desktop/SKILL.md) gives selection criteria and an operating
workflow. It can be read directly, copied into an Agent Skills client, or installed through
`anode configure`. The skill does not download software or change settings when loaded.

Anode requires Windows 10/11 Pro, Enterprise or Education, or a Windows Server RDP host.
The release targets x64. Use [the installation guide](INSTALL.md) within the user's authorized
scope; `anode doctor` reads prerequisites, while `anode setup` changes machine settings through UAC.

## Discovery after installation

```powershell
anode guide | Out-Host          # selection and operating guide, no seat required
anode guide --json | Out-Host   # name, version, instructions, guide and docs URL
anode configure | Out-Host     # register installed CLIs and the discoverable skill
```

The MCP server's initialization message describes when to use Anode. Tool names, descriptive
titles and task-oriented descriptions help clients find it through tool search. Search for
**Anode background Windows desktop**, **native UI automation**, or **headed app testing**.

`anode_guide` returns the same workflow embedded in the executable, even before setup and while
other tools are busy. `seat_status` is a read-only observation and returns `state: stopped` when
there is no daemon. It no longer creates a desktop. Call `seat_start` when the task needs one.
Other live-seat tools still auto-start a hidden seat where documented.

Tool annotations are conservative: only guide/status advertise read-only behavior. Capture and
inspection tools can start a seat, and input or application commands can affect shared files and
services. Annotations describe effects; they are not an approval override or a guarantee that
timed-out actions are safe to replay. See the [MCP tool specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

## Skill installation and control

`anode configure` installs `anode-desktop` for each selected client:

| Client | Personal skill folder |
| --- | --- |
| Codex | `%USERPROFILE%\.agents\skills\anode-desktop` |
| Claude Code | `%USERPROFILE%\.claude\skills\anode-desktop` (or `%CLAUDE_CONFIG_DIR%\skills\anode-desktop`) |

These use the documented [Codex skill locations](https://learn.chatgpt.com/docs/build-skills)
and [Claude Code skill locations](https://code.claude.com/docs/en/skills). A custom `CODEX_HOME`
still controls Codex's MCP config; the personal skill location is under the user profile.

The skill is eligible for automatic selection. Clients decide whether to load it; installation
cannot guarantee a model will choose Anode. Invoke it explicitly as `$anode-desktop` in Codex
or `/anode-desktop` in Claude Code when useful.

For MCP registration alone, use `anode configure --no-skill` or
`scripts\connect-agents.ps1 -NoSkill`. This leaves an existing skill intact. To remove the skill,
delete only its `anode-desktop` folder from the location above; MCP registration is independent.

The connector records hashes in `.anode-skill.json`. Future configuration runs update a managed
copy only when those files are unchanged, backing up replaced files beside their originals.
Existing custom skills or edited copies are preserved with a message pointing to the packaged
source. It never edits global `AGENTS.md`, `CLAUDE.md`, tool allowlists or approval policies.

## A small desktop task

For an authorized request to test a native app:

1. `seat_status`, then `seat_start` if stopped.
2. `seat_capabilities` to check actual capture/input blockers.
3. `seat_run` to launch an owned fixture; `seat_windows` to locate its actual window.
4. `seat_observe` to discover controls; `seat_element` with a fresh snapshot for offered actions.
5. `seat_wait` for the resulting state, or a fresh screenshot when pixels matter.
6. Close the owned fixture. Keep unrelated apps and jobs running.

For delayed UI, use waits. For builds/servers, save command job IDs and read incremental output.
For visual-only controls, use a fresh screenshot and original-screen coordinates. Keep the viewer
hidden unless the user wants it. If capture fails, use accessible controls or diagnose the failure;
do not switch to the main desktop. `seat_stop` closes **all** apps in the shared seat.

The [skill](../skills/anode-desktop/SKILL.md) has the full operating workflow;
[desktop tools](DESKTOP-TOOLS.md) and [development testing](DEVELOPMENT-TESTING.md) provide examples.
