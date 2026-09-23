# Investigation records

These reports preserve the diagnosis, repairs and experiments from September 12–14,
2026. They are historical records, not a list of steps to run on every machine.
Account names, email addresses, machine names, user SIDs and certificate thumbprints
have been replaced with examples before publication.

Implemented fixes include listener-based readiness, service restart when needed,
detached-daemon logging, prompt startup failures, RDP interop corrections, verified
child-session identity, cancellable transport/MCP handling, and direct Steam launch
protection. Anode 0.2 also adds background window/control inspection, actions and
HTML reports; see [Desktop tools](../docs/DESKTOP-TOOLS.md).

Hidden capture and changing browser screenshots were verified after restarting the
updated parent daemon. A separate GameInput helper blocked focus/input until a
user-approved child-helper repair; actual mouse and keyboard delivery then passed.
See the [GameInput report](2026-09-14-gameinput-helper-blocks-seat-input.md).
These checks do not establish game-controller support. Liftoff and an
isolated Steam client were launched in the seat while the main Steam client stayed
running; gameplay and the vision pilot have not been validated.

The [September 18 record](2026-09-18-viewer-moves-the-real-pointer.md) covers the viewer moving the
real mouse pointer when a seat program called `SetCursorPos`, the measurements that showed the
Remote Desktop control only checks whether the pointer is over its rectangle, and the import gate
that fixes it. Its live verification against a restarted daemon is still outstanding.

The [September 23 record](2026-09-23-steam-stays-on-the-desktop.md) supersedes the September 14
conclusion that a game in the seat cannot reach the desktop's Steam client: only the names of
Steam's handshake objects are bound to a session, and the seat now links them. Liftoff launched
that way ran in the seat against the desktop's Steam, which never moved.

`fix-rdp-listener.ps1` is the administrator script used for a specific private-key
permission failure. It creates and binds a certificate, grants NETWORK SERVICE
read access to that certificate's key, and restarts Remote Desktop Services. It
is not executed by `anode setup`. Keep existing certificate/binding information
and arrange any RDP disruption before using it on a similarly diagnosed machine.
