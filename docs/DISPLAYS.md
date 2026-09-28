# Test other displays

The seat starts at 1280x720 and 100% scaling, or at whatever `anode start --width N --height N --scale N`
asked for. Agents can change its resolution and Windows scaling while it runs, to see how an app
behaves on a small laptop, a 4K monitor or a portrait screen, without restarting the seat or closing
anything in it.

**Not verified live yet:** builds and quick self-tests pass, and the self-test confirms on the real
Remote Desktop control that the display-change call and the scaling setting exist. Changing a running
seat's display has not been tried in a live seat, because the seat was busy with other work. The
opt-in test below still needs to run when a seat is free.

## Agent tool

`seat_display` changes the display. Only the fields you pass change:

| Argument | Meaning |
| --- | --- |
| `width`, `height` | The new size in physical pixels, together. The width is even, 640-8192; the height 480-8192. |
| `scale` | Windows scaling in percent: 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450 or 500. |
| `reset` | `true` restores the display the seat started with. It takes no size or scale. |
| `screenshot` | `true` adds a picture of the new display to the result, taken once apps have had a moment to redraw. |
| `maxWidth` | The screenshot's width limit; default 1280. Coordinates stay seat pixels. |

```json
{"width": 1920, "height": 1080, "scale": 150}
{"scale": 200}
{"width": 1080, "height": 1920, "screenshot": true}
{"reset": true}
```

The result is the display Windows applied: `width`, `height`, `scale`, `effectiveWidth` and
`effectiveHeight` (the size a DPI-unaware app, or a web page in CSS pixels, lays out in), the
`previous` and `startup` displays, `changed`, `method` (`live`, `reconnect` or `none`) and a
`summary`. Screenshots, click coordinates and control bounds use the new size straight away. Window
IDs stay valid, but observations do not: windows and controls move, so observe again before acting.

When Windows applies something other than what was asked, for example a smaller scale, the call fails
with `errorCode: "display_not_applied"` and its `result` still describes the display the seat now has.
Anode's viewer keeps that display too: a later Reconnect or sign-in asks for it, not for the request
that failed.

`seat_display` needs the desktop lease, like input and screenshots. `seat_status` reports the current
display under `seat.screen`, with its `scale`, and the startup display as `startupDisplay`, without a
lease. The `display_test` prompt asks an agent to run an app through a set of displays and report
what breaks at each.

A useful spread, unless the app's users say otherwise:

| Display | Stands for |
| --- | --- |
| 1024x768 at 100% | the smallest screens still around; also any window that must fit one |
| 1366x768 at 100% | inexpensive laptops |
| 1920x1080 at 100% | the most common desktop monitor |
| 1920x1080 at 150% | a laptop whose Windows recommends 150% |
| 2560x1440 at 125% | a larger desktop monitor |
| 3840x2160 at 200% | a 4K monitor or high-end laptop |
| 1080x1920 at 100% | a monitor turned to portrait |

## CLI

```powershell
anode display                                # current and startup display; no lease needed
anode display 1920x1080 --scale 150          # these need the lease (ANODE_AGENT_ID, ANODE_LEASE_TOKEN)
anode display --scale 200 --shot scaled.png
anode display reset
```

`--shot` saves a full-size screenshot of the new display and `--json` prints the result. See
[multiple agents](MULTI-AGENT.md#cli-workflow) for taking the lease from a terminal.

## What happens

The Remote Desktop control in Anode's viewer gives the seat its display, so the daemon makes each
change and the seat host measures the seat's own screen to confirm it. Anode first asks for the new
display live, through Remote Desktop's display-control channel, as a monitor change. Apps get the same
display-change and DPI-change messages as on a real PC: per-monitor aware apps lay themselves out
again, and the rest are stretched by Windows. Apps that read scaling only at startup keep their old
layout until they restart, so **start the app under test again** after changing the scale to see how
it starts at that scale.

When the display does not change live within a few seconds, Anode reconnects its viewer at the new
size instead. The seat's session and every app in it keep running; only the connection to them is
renewed. If that reconnection fails, Anode connects the viewer at the display the seat had and
reports the failure. A change takes a few seconds either way, a reconnect up to about a minute.

A seat that signed in through the Windows credential dialog (`--sign-in`, or **Sign in…** in the
viewer) would need your password again to reconnect. Anode never opens that dialog for an agent, so
there it leaves the viewer connected and reports the display Windows did not apply live. To test such
a display, start a seat at it with `anode start --sign-in --width N --height N --scale N`.

A change lasts while its agent holds the lease. When that lease is released or expires, the seat goes
back to its startup display before the next agent's first action runs, so nobody inherits another
agent's test display. Later viewer reconnections keep the chosen display, and a stopped seat starts
again at the startup display.

The seat host and its inspection worker see physical pixels at every scale, so a screenshot at 150%
has the display's real resolution, and click coordinates and control bounds are in the same pixels.

The viewer keeps its window size and fits the seat's new shape inside it, on its dark background.

## Limits

- One display. Anode does not create a second monitor, so multi-monitor layouts cannot be tested.
- Portrait is a tall size such as 1080x1920; the display's rotation flag stays landscape.
- Widths are even because Remote Desktop's display-control channel requires it.
- Scaling is one of Windows' own steps. Windows can refuse a scale that leaves too little room; the
  result then says what it applied.
- Games and 3D apps may reset their rendering when the display changes, as on a real PC. Some keep
  their own resolution setting and need it changed in the game.
- Larger displays cost the seat more memory and make bigger screenshots; pass `maxWidth` to keep
  images cheap while polling.

`scripts/test-display.ps1` is an opt-in live test for a ready seat with an existing lease. It walks the
seat through several displays, checks that status and screenshots follow each one, resets the display
and checks that the startup display is back. It never starts or stops the seat or opens its viewer.
Do not run it while the seat is busy with other work; apps in the seat see each display change.
`selftest --quick` covers the rest with a stand-in daemon and screen, without changing any display.
