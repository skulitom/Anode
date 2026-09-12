using System.Text.Json.Nodes;

namespace Anode.Mcp;

/// <summary>
/// The tool surface an agent sees. Each entry maps one MCP tool onto one operation on
/// the control pipe, so there is no second implementation to keep in step with the CLI.
///
/// Descriptions are written for a model that has never seen this machine: they say what
/// the tool does to the seat, and where the seat is not the user's screen.
/// </summary>
internal static class Tools
{
    private sealed record Tool(string Name, string Op, bool StartsDaemon, string Description, JsonObject Schema);

    private static readonly Tool[] All =
    {
        new("seat_status", "status", true,
            "Report the state of the seat: whether it is ready, which Windows session it is, its screen size, "
            + "and whether Steam would launch games into it. Call this first.",
            Schema()),

        new("seat_start", "seat.start", true,
            "Bring the seat up if it is not already running. Returns when the seat has signed in and its agent answers.",
            Schema()),

        new("seat_stop", "seat.stop", false,
            "Sign the seat out. Every program running in it closes immediately, including frozen ones. "
            + "The user's own session is untouched. Use this when you are finished, or when something in the seat is stuck.",
            Schema(("reason", "string", "Why the seat is being stopped. Shown to the user.", false))),

        new("seat_screenshot", "screenshot", true,
            "Capture the seat's screen and return it as an image. This is the seat's desktop, never the user's monitors. "
            + "Prefer maxWidth around 1000 and format jpeg while polling, to keep the image cheap.",
            Schema(
                ("maxWidth", "integer", "Scale the image down to at most this many pixels wide. Click coordinates still use the captured size.", false),
                ("format", "string", "png (default) or jpeg.", false),
                ("quality", "integer", "JPEG quality, 1 to 100. Default 80.", false))),

        new("seat_run", "run", true,
            "Start a program inside the seat. It opens on the seat's desktop, not on the user's screen. "
            + "Use a full path, a shortcut, or anything Windows can open.",
            Schema(
                ("path", "string", "Full path to the program, document or shortcut.", true),
                ("args", "array", "Arguments to pass to the program.", false),
                ("cwd", "string", "Working directory. Defaults to the program's folder.", false))),

        new("steam_status", "steam.status", true,
            "Report where Steam is running and therefore where a game would open. Steam allows one instance per Windows "
            + "user, so if Steam is already running outside the seat, games open there instead.",
            Schema()),

        new("steam_launch", "steam.launch", true,
            "Start a Steam game inside the seat by its app id. If Steam is not running anywhere, Steam is started inside "
            + "the seat first so the game lands there. If Steam is already running outside the seat this fails with an "
            + "explanation, unless force is true.",
            Schema(
                ("appId", "integer", "Steam app id, the number in the game's store URL.", true),
                ("force", "boolean", "Launch even though Steam is running outside the seat. The game will probably open on the user's screen.", false),
                ("args", "array", "Extra arguments for the game.", false))),

        new("seat_processes", "ps.list", true,
            "List the programs running in the seat.",
            Schema(("windowedOnly", "boolean", "Only programs with a window. Default true.", false))),

        new("seat_kill_process", "ps.kill", false,
            "Close one program inside the seat, by process id or by name. Refuses to touch anything outside the seat.",
            Schema(
                ("pid", "integer", "Process id, from seat_processes.", false),
                ("name", "string", "Executable name, for example notepad.", false))),

        new("seat_click", "input.click", true,
            "Click in the seat. Coordinates are pixels on the seat's screen, with 0,0 at the top left. "
            + "Omit x and y to click wherever the seat's pointer already is.",
            Schema(
                ("x", "integer", "X in seat pixels.", false),
                ("y", "integer", "Y in seat pixels.", false),
                ("button", "string", "left (default), right, middle, x1 or x2.", false),
                ("count", "integer", "1 for a click, 2 for a double click.", false))),

        new("seat_move", "input.move", true,
            "Move the seat's mouse pointer. The user's own pointer does not move.",
            Schema(
                ("x", "integer", "X in seat pixels.", false),
                ("y", "integer", "Y in seat pixels.", false),
                ("dx", "integer", "Relative move instead of absolute. Useful for games that read raw mouse motion.", false),
                ("dy", "integer", "Relative move instead of absolute.", false))),

        new("seat_drag", "input.drag", true,
            "Press a mouse button at one point in the seat, move to another, and release.",
            Schema(
                ("fromX", "integer", "Start X.", true),
                ("fromY", "integer", "Start Y.", true),
                ("toX", "integer", "End X.", true),
                ("toY", "integer", "End Y.", true),
                ("button", "string", "left (default), right or middle.", false))),

        new("seat_scroll", "input.scroll", true,
            "Scroll the wheel in the seat. Negative scrolls down.",
            Schema(
                ("amount", "integer", "Wheel notches. Negative is down. Default -3.", false),
                ("horizontal", "boolean", "Scroll sideways instead.", false))),

        new("seat_key", "input.key", true,
            "Press a key or a chord in the seat, for example \"enter\", \"f5\" or \"ctrl+shift+esc\". "
            + "Keys are sent as scan codes so games see them.",
            Schema(
                ("keys", "string", "A key name, or names joined with +.", true),
                ("holdMs", "integer", "How long to hold the key down. Default 40.", false))),

        new("seat_type", "input.text", true,
            "Type literal text into whatever has focus in the seat.",
            Schema(
                ("text", "string", "The text to type.", true),
                ("perCharMs", "integer", "Delay between characters, for programs that drop fast input.", false))),

        new("gamepad_attach", "gamepad.attach", true,
            "Plug a virtual Xbox 360 controller into the machine. Games in the seat see it as a real controller. "
            + "Note that it is a machine-wide device, so it is visible to the user's session too.",
            Schema(("slot", "integer", "Controller slot, 0 to 3. Default 0.", false))),

        new("gamepad_detach", "gamepad.detach", false,
            "Unplug the virtual controller.",
            Schema(("slot", "integer", "Controller slot. Default 0.", false))),

        new("gamepad_set", "gamepad.set", true,
            "Set the virtual controller's state. Only the fields you pass change, so you can hold a stick while tapping "
            + "buttons. Sticks take -1 to 1; triggers take 0 to 1.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slot"] = Property("integer", "Controller slot. Default 0."),
                    ["buttons"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Button name to pressed state. Names: a, b, x, y, lb, rb, back, start, guide, ls, rs, up, down, left, right.",
                        ["additionalProperties"] = new JsonObject { ["type"] = "boolean" }
                    },
                    ["axes"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Stick axes lx, ly, rx, ry, each -1 to 1. On Xbox pads, ly and ry are positive upwards.",
                        ["additionalProperties"] = new JsonObject { ["type"] = "number" }
                    },
                    ["triggers"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Triggers lt and rt, each 0 to 1.",
                        ["additionalProperties"] = new JsonObject { ["type"] = "number" }
                    }
                }
            }),

        new("gamepad_tap", "gamepad.tap", true,
            "Press one controller button or trigger briefly and release it.",
            Schema(
                ("button", "string", "a, b, x, y, lb, rb, back, start, guide, ls, rs, up, down, left, right, lt or rt.", true),
                ("ms", "integer", "How long to hold it. Default 80.", false),
                ("slot", "integer", "Controller slot. Default 0.", false))),

        new("gamepad_reset", "gamepad.reset", false,
            "Release every button and centre every stick on the virtual controller.",
            Schema(("slot", "integer", "Controller slot. Default 0.", false))),

        new("seat_show", "seat.show", false,
            "Show the viewer window so the user can watch the seat.",
            Schema()),

        new("seat_hide", "seat.hide", false,
            "Hide the viewer window. The seat keeps running.",
            Schema())
    };

    public static JsonArray Definitions()
    {
        var array = new JsonArray();
        foreach (var tool in All)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone()
            });
        }
        return array;
    }

    public static bool TryResolve(string name, out string op, out bool startsDaemon)
    {
        foreach (var tool in All)
        {
            if (!tool.Name.Equals(name, StringComparison.Ordinal)) continue;
            op = tool.Op;
            startsDaemon = tool.StartsDaemon;
            return true;
        }
        op = string.Empty;
        startsDaemon = false;
        return false;
    }

    private static JsonObject Property(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in fields)
        {
            var property = Property(field.Type, field.Description);
            if (field.Type == "array") property["items"] = new JsonObject { ["type"] = "string" };
            properties[field.Name] = property;
            if (field.Required) required.Add(field.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }
}
