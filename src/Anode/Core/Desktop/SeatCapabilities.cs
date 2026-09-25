using System.Text.Json.Nodes;
using Anode.Core.Capture;
using Anode.Core.Input;
using Anode.Core.Session;

namespace Anode.Core.Desktop;

internal static class SeatCapabilities
{
    // Called only by the disposable worker after verifying its child session.
    public static JsonObject Read(bool probeCapture)
    {
        var size = InputInjector.ScreenSize();
        var capture = new JsonObject { ["tested"] = probeCapture, ["available"] = null };
        string? inputWarning = WindowAccess.InputBlockReason();
        if (probeCapture)
        {
            try
            {
                var shot = ScreenCapture.Capture(128, "png");
                capture["available"] = true;
                capture["sourceWidth"] = shot.SourceWidth;
                capture["sourceHeight"] = shot.SourceHeight;
            }
            catch (Exception ex) { capture["available"] = false; capture["error"] = ex.Message; }
        }
        return new JsonObject
        {
            ["version"] = typeof(SeatCapabilities).Assembly.GetName().Version?.ToString(3),
            ["session"] = ChildSession.CurrentSessionId(), ["workingDirectory"] = Environment.CurrentDirectory,
            ["screen"] = new JsonObject { ["width"] = size.Width, ["height"] = size.Height }, ["capture"] = capture,
            ["input"] = new JsonObject { ["deliveryVerified"] = false, ["knownBlocker"] = inputWarning is not null, ["warning"] = inputWarning },
            ["supported"] = new JsonArray("window discovery", "UI Automation", "control actions", "UI waits", "mouse and keyboard", "command output and jobs"),
            ["limitations"] = new JsonArray("App accessibility varies; custom canvases need screenshots and input.",
                "Capture availability is a point-in-time probe, not a rendering guarantee.",
                "Secure desktops, UAC and password controls require direct user interaction.",
                "Files, accounts, application singletons and network ports are shared with the parent session.",
                "Virtual gamepads stay inside the seat only when HidHide is installed; `gamepad state` reports it. Execution jobs end when the seat host exits."),
            ["summary"] = $"Seat {ChildSession.CurrentSessionId()}, Anode {typeof(SeatCapabilities).Assembly.GetName().Version?.ToString(3)}. "
                + (probeCapture ? $"Capture {(capture["available"]!.GetValue<bool>() ? "available" : "unavailable: " + capture["error"])}. " : "Capture not tested. ")
                + "Window/control inspection, UI waits and command jobs are supported. App accessibility varies; files and ports are shared."
                + (inputWarning is null ? " Input delivery requires an app-specific check." : "\n" + inputWarning)
        };
    }
}
