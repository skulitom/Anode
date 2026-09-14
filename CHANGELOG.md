# Changelog

## 0.3.0 — 2026-09-14

- Added command jobs with separate stdout/stderr, exit codes, incremental cursors,
  cancellation, hard lifetimes and cleanup of owned descendants.
- Added condition waits with fresh actionable observations, measured capture/input
  diagnostics and 30 MCP tools. Added a window raise action and reliable focus from
  disposable workers, with explicit errors when Windows refuses focus.
- Removed the initial viewer flash from hidden startup. Verified sustained hidden
  capture after activating the per-user rendering preference.
- Identified a SYSTEM GameInput helper blocking focus and synthetic input. Added a
  precise diagnostic, refusal of ineffective input and an opt-in administrator
  repair that stops only the affected child-session helper.
- Added repeatable Codex/Claude CLI registration with config backups and timeouts,
  a development/testing guide and Claude repository instructions.
- Expanded verification to 38 quick checks, 15 native fixture checks, and a browser/
  command suite covering headed Chrome, form/HTTP behavior, mobile layout, capture,
  actual Anode mouse/keyboard input, working directories, environment and exit codes.

Live checks preserved the hidden viewer, main Steam client, boxed Steam client and
Liftoff. The one-time GameInput repair restored input; the Windows service may recreate
its helper, so this is not a permanent service configuration change. Gamepad gameplay,
the vision pilot and application types beyond the recorded fixtures remain unverified.

## 0.2.0 — 2026-09-14

- Added window discovery, UI Automation control trees, bounded text extraction and
  direct window/control actions through CLI and MCP (26 tools in total).
- Added compact text summaries, structured MCP results and searchable standalone
  HTML inspection reports with optional screenshots and control bounds.
- Kept accessibility in verified child-session workers with deadlines, expiring
  references, password-value omission and no replay of uncertain actions.
- Made automatic CLI/MCP daemon startup hide the viewer. Added a reversible
  per-user RDP rendering preference and hide-on-minimize behaviour. Hidden capture
  still requires validation on the reported machine after a daemon restart.
- Fixed listener readiness, service restart when setup requires it, detached-daemon
  log paths, actionable startup errors, RDP interop and child-session verification.
- Added prompt-free unattended failure handling and explicit `--sign-in` support.
- Hardened pipe deadlines, cancellation, access control and MCP scheduling so Stop
  remains available while ordinary tool work is busy.
- Guarded direct Steam launches when a client is running outside the seat.
- Added 33 quick checks, a 13-check opt-in desktop fixture, Windows CI, investigation
  records and expanded setup, troubleshooting and agent documentation.

Live validation covers control automation with a hidden viewer. Liftoff launch in
an isolated Steam client was observed; gameplay and the vision pilot remain unverified.
