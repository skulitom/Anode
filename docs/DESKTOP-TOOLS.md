# Inspect and control the background desktop

Anode can enumerate windows, read accessible controls and text, and act on those
controls inside the verified child session. This works through the CLI and MCP
without a computer-use overlay on your main desktop.

## A typical workflow

Start a seat with `anode start --hidden`, then use:

```powershell
anode windows --query notepad
anode inspect <windowId>
anode inspect <windowId> --html inspection.html
anode element <snapshotId> <elementId> set_value --value "Hello from Anode"
anode inspect <windowId>
```

Use the identifiers returned by the preceding command. `inspect` prints a compact
control tree with names, roles, text, states and available actions. `--json` returns
structured data. `--html` writes a standalone report with searchable controls and,
when capture is available, a screenshot with control bounds highlighted on hover.
`--image screen.png` also saves the screenshot. Reports are observations, not live
control panels: act through Anode using a fresh inspection.

The CLI reads the tree without capturing an image unless `--html` or `--image` is
requested. MCP `seat_observe` includes a screenshot by default; set
`includeScreenshot: false` for a smaller response or when capture is unavailable.
MCP returns a readable summary, structured control data and an image block when
available. Connecting Anode does not redirect other computer-use tools into the
seat: select the `seat_*` tools explicitly.

## Available actions

| Tool | What it does |
| --- | --- |
| `seat_windows` | Lists visible seat windows, including minimized ones; filters by title/process `query` or `pid`. Returns window IDs, process names, bounds and window state. |
| `seat_observe` | Reads one window's control tree, text and state, plus an optional seat screenshot. |
| `seat_window` | Requests `focus`, `raise`, `restore`, `maximize`, `minimize`, `close`, or `move`. Raise changes stacking without requesting focus. Moving requires `x`, `y`, `width`, `height`. Focus stays inside the seat. |
| `seat_element` | Acts on an observed control using `snapshotId`, `elementId` and one of its offered actions. |

| Control action | Extra arguments | Typical control |
| --- | --- | --- |
| `invoke` | None | Button or menu item |
| `set_value` | `value`, including an empty string | Editable text field |
| `toggle` | None | Checkbox |
| `select` | None | List item or tab |
| `expand`, `collapse` | None | Tree item or menu |
| `scroll_into_view` | None | Offscreen list item |
| `scroll` | `direction`: up/down/left/right; optional `amount`: small/large | Scrollable container |
| `set_range` | Finite `number` | Slider or numeric control; inspect its `range` first |
| `focus` | None | Focusable control |

CLI equivalents use `anode window <windowId> <action>` or
`anode element <snapshotId> <elementId> <action>`, with extra arguments prefixed
by `--`, for example `--direction down --amount large` or `--number 50`.

Only use actions listed on that control. Read-only fields do not offer value
changes. Password controls omit their values and offer no element actions.
Successful action submission is not proof that an application completed its work;
inspect again to confirm the result, including possible modal dialogs.

## References, limits and timeouts

Window IDs expire after ten minutes; listing a window refreshes its reference.
Snapshots expire after 90 seconds and are consumed by one attempted element
action. Window actions and other input invalidate existing snapshots. A timed-out
action may already have happened: inspect again instead of replaying it.

Observations default to 200 controls, depth 8 and 6,000 text characters. MCP accepts
`maxElements` (1–500), `maxDepth` (0–20), `maxTextChars` (0–20,000), and
`includeOffscreen`. CLI uses `--max-elements`, `--max-depth`, `--max-text` and
`--offscreen`. The tree reports truncation and provider warnings. Accessibility
work runs in a disposable child-session process with a ten-second deadline, so
an unresponsive app provider does not hold the seat indefinitely.

The screenshot covers the seat desktop, not just the selected window. Bounds and
click coordinates use the original screen dimensions, regardless of image scaling.
Window handles and UI Automation runtime references stay private to the host.

## Limits to expect

Applications decide how much accessibility information they expose. Native Windows
controls often expose useful text and actions; games and custom canvases may expose
only an outer window. Existing Anode screenshot, mouse and keyboard tools remain
available for those interfaces. This is not a browser DOM API, OCR service, or a
way to interact with UAC and secure sign-in screens.

If screen capture fails, `seat_observe` still returns available controls and an
explicit `screenshotError`. It never opens the parent viewer or captures your main
desktop as a fallback. Do not guess coordinates from a missing or stale image.
See [capture troubleshooting](TROUBLESHOOTING.md#hidden-viewer-capture-fails).

Anode shares your user profile and installed apps. Some applications reuse an
existing instance in another session. Steam needs special handling when its main
client must remain on your desktop; see the Steam notes in the README. Virtual
gamepads remain visible across Windows sessions.

## Verification

`anode selftest --quick` checks argument validation, reference expiry, no replay,
worker timeouts, presentation escaping and MCP discovery without accessing a
desktop. To run the opt-in integration test against an already ready seat:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-desktop.ps1 -Anode .\dist\anode.exe
```

The test launches a disposable fixture inside the verified seat, exercises its
controls, checks password omission and the parent viewer's unchanged state, saves
a report under `artifacts/desktop-test/`, then closes only its fixture.
