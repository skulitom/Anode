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

Anode requires 64-bit Windows 10/11 Pro, Enterprise or Education, or a Windows Server RDP host.
Windows Home is unsupported. Use [the installation guide](INSTALL.md) within the user's authorized
scope; `anode doctor` reads prerequisites, while `anode setup` changes machine settings through UAC.

## Discovery after installation

```powershell
anode guide | Out-Host          # selection and operating guide, no seat required
anode guide --json | Out-Host   # name, version, description, instructions, guide and docs URL
anode configure | Out-Host      # register installed CLIs and the discoverable skill
```

The MCP server's initialization message describes when to use Anode and the lease workflow.
Tool names, descriptive titles and task-oriented descriptions help clients find it through tool
search. Search for **Anode background Windows desktop**, **native UI automation**, or
**headed app testing**.

The server also offers two MCP prompts, which clients show as slash commands. `desktop_test`
asks the agent to test the app or task the user names, following the workflow below.
`desktop_guide` returns the full guide. Claude Code's `/` menu lists them as
`/anode:desktop_test (MCP)` and `/anode:desktop_guide (MCP)`, and typing
`/mcp__anode__desktop_test` also runs one; VS Code uses `/mcp.anode.desktop_test` and
`/mcp.anode.desktop_guide`. The names follow the server name in the client's configuration.

`anode_guide` returns the same workflow embedded in the executable, even before setup and while
other tools are busy. Prompts are also answered locally.

## Starting a seat

`seat_status` is read-only: it never starts a daemon or seat and returns `state: stopped` when
Anode is not running. Call `seat_lease` with `{"action":"acquire"}` (or `seat_start`) when the
task needs a desktop; only those two start a seat, and they start it with the viewer hidden.
Other tools that need a daemon report that Anode is not running instead of starting it. On the
CLI, only `anode start`, `anode up` and `anode lease acquire` start a seat.

Desktop tools are refused until you hold a lease. A seat started by `seat_start` still needs
`seat_lease` acquisition before screenshots, observation, input, launches or command jobs.

## Tool annotations

Annotations help clients decide which calls need an approval prompt:

| Group | Hints | Tools |
| --- | --- | --- |
| Read-only | `readOnlyHint: true`, `openWorldHint: false` | `anode_guide`, `seat_status`, `seat_capabilities`, `seat_processes`, `steam_status` |
| Read-only, seat content | `readOnlyHint: true`, `openWorldHint: true` (returns untrusted app and web content) | `seat_windows`, `seat_observe`, `seat_screenshot`, `seat_wait` |
| Additive | `readOnlyHint: false`, `destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false` | `seat_start`, `seat_hide` |
| Conservative | `destructiveHint: true`, `idempotentHint: false`, `openWorldHint: true` | every other tool |

Read-only tools observe and never start a seat. Some still need your lease: `seat_windows`,
`seat_observe`, `seat_screenshot` and `seat_wait`. `seat_start` and `seat_hide` at most start a
hidden seat or hide the viewer. The conservative group changes the shared desktop, files, jobs or
machine: input, launches, command jobs, window and element actions, process termination, Steam
launches, the virtual gamepad, `seat_lease` (release can cancel jobs), `seat_stop`, and
`seat_show`, which puts the viewer on the user's screen. Annotations describe effects; they are
not an approval override or a guarantee that timed-out actions are safe to replay. See the
[MCP tool specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

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

The [Claude Code plugin](CONNECTING-AGENTS.md#1-claude-code) bundles the same skill as
`/anode:anode-desktop` with the MCP server. Use the plugin or `anode configure claude`, not both;
otherwise the tools and the skill appear twice.

For MCP registration alone, use `anode configure --no-skill` or
`scripts\connect-agents.ps1 -NoSkill`. This leaves an existing skill intact. To remove the skill,
delete only its `anode-desktop` folder from the location above; MCP registration is independent.

The connector records hashes in `.anode-skill.json`. Future configuration runs update a managed
copy only when those files are unchanged, backing up replaced files beside their originals.
Existing custom skills or edited copies are preserved with a message pointing to the packaged
source. It never edits global `AGENTS.md`, `CLAUDE.md`, tool allowlists or approval policies.

## A small desktop task

For an authorized request to test a native app:

1. `seat_status`. It never starts anything.
2. `seat_lease {action: "acquire"}`. Acquisition starts a hidden seat if needed and the MCP
   server keeps the token. If another agent owns the desktop, it waits in a first-come line for
   up to `waitSeconds` (default 30); calling it again within 5 seconds keeps your place. Each
   desktop action keeps the lease for its lifetime (default 120 seconds, `ttlSeconds` 10 to 600);
   renew with `{action: "renew"}` only across longer pauses.
3. `seat_capabilities` to check actual capture/input blockers.
4. `seat_run` to launch an owned fixture; `seat_windows` to locate its actual window.
5. `seat_observe` to discover controls; `seat_element` with a fresh snapshot for offered actions.
   `seat_wait` for the resulting state, or a fresh screenshot when pixels matter.
6. Close the owned fixture and cancel owned command jobs. Keep unrelated apps and jobs running.
7. `seat_lease {action: "release"}`, so the next agent in line gets the desktop. `cancelJobs: true`
   requests cancellation only for your command jobs.

For delayed UI, use waits. For builds/servers, save command job IDs and read incremental output.
For visual-only controls, use a fresh screenshot and original-screen coordinates. Keep the viewer
hidden unless the user wants it. If capture fails, use accessible controls or diagnose the failure;
do not switch to the main desktop. `seat_stop` closes **all** apps in the shared seat.
[Desktop leases](TROUBLESHOOTING.md#desktop-leases) explains `seat_busy`, `lease_expired` and
`stale_lease` errors.

The [skill](../skills/anode-desktop/SKILL.md) has the full operating workflow;
[multiple agents](MULTI-AGENT.md) covers leases and recovery;
[desktop tools](DESKTOP-TOOLS.md) and [development testing](DEVELOPMENT-TESTING.md) provide examples.
