using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Capture;
using Anode.Core.Gamepad;
using Anode.Core.Input;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Cli;

/// <summary>
/// Exercises the parts of Anode that do not need a seat, so a machine can be checked
/// before anyone enables anything. Nothing here injects keyboard or mouse input, and
/// nothing here changes a setting.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var options = new Args(args);
        bool quick = options.Flag("quick");
        bool includeGamepad = !quick && !options.Flag("no-gamepad");
        bool includeEnvironment = !quick && !options.Flag("no-scheduler");
        var results = new List<(string Name, bool Ok, string Detail)>();

        Check(results, "key names", () =>
        {
            (string Name, ushort Expected)[] cases =
            {
                ("a", 0x41), ("Z", 0x5A), ("5", 0x35), ("enter", 0x0D), ("esc", 0x1B),
                ("space", 0x20), ("f5", 0x74), ("f12", 0x7B), ("ctrl", 0xA2), ("shift", 0xA0),
                ("alt", 0xA4), ("win", 0x5B), ("up", 0x26), ("num7", 0x67), ("tab", 0x09)
            };
            foreach (var (name, expected) in cases)
            {
                ushort actual = KeyCodes.Resolve(name);
                if (actual != expected)
                    throw new InvalidOperationException($"'{name}' resolved to 0x{actual:X2}, expected 0x{expected:X2}");
            }
            try
            {
                KeyCodes.Resolve("definitely-not-a-key");
                throw new InvalidOperationException("an unknown key name was accepted");
            }
            catch (ArgumentException) { }
            return $"{cases.Length} names resolve, unknown names rejected";
        });

        Check(results, "gamepad mapping", () =>
        {
            foreach (string name in new[] { "a", "b", "x", "y", "lb", "rb", "back", "start", "guide", "ls", "rs", "up", "down", "left", "right" })
                GamepadManager.ResolveButton(name);
            foreach (string name in new[] { "lx", "ly", "rx", "ry" }) GamepadManager.ResolveAxis(name);
            foreach (string name in new[] { "lt", "rt" }) GamepadManager.ResolveTrigger(name);
            return "15 buttons, 4 axes, 2 triggers";
        });

        Check(results, "CLI help routing", CliChecks.HelpRouting);
        Check(results, "CLI argument checks", CliChecks.Arguments);
        Check(results, "CLI help and messages", CliChecks.HelpText);
        Check(results, "CLI mcp --help", CliChecks.McpHelp);

        if (includeEnvironment) Check(results, "screen capture", () =>
        {
            var shot = ScreenCapture.Capture(maxWidth: 640, format: "jpeg", jpegQuality: 70);
            if (shot.Bytes.Length < 512) throw new InvalidOperationException("the encoder produced an implausibly small image");
            return $"{shot.SourceWidth}x{shot.SourceHeight} -> {shot.Width}x{shot.Height}, {shot.Bytes.Length / 1024} KB jpeg";
        });

        if (includeEnvironment) Check(results, "screen geometry", () =>
        {
            var size = InputInjector.ScreenSize();
            if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("no usable screen size");
            return $"{size.Width}x{size.Height}";
        });

        Check(results, "pipe round trip", () =>
        {
            string name = $"anode-selftest-{System.Environment.ProcessId}";
            using var server = new JsonPipeServer(name, request =>
                Task.FromResult(JsonLine.Ok(new JsonObject { ["echo"] = request.Str("value") })));
            server.Start();

            using var client = JsonPipeClient.TryConnectAsync(name, 4000).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("could not connect to the test pipe");

            var response = client.RequestAsync("test", new JsonObject { ["value"] = "hello" }, 4000).GetAwaiter().GetResult();
            if (response.Obj("result")?.Str("echo") != "hello")
                throw new InvalidOperationException($"unexpected reply: {response.ToJsonString()}");

            var failure = client.RequestAsync("test", new JsonObject(), 4000).GetAwaiter().GetResult();
            if (failure.Bool("ok") != true) throw new InvalidOperationException("the echo handler failed");
            return "request and response over a named pipe";
        });

        Check(results, "pipe queue deadline", () => TransportChecks.QueueDeadline().GetAwaiter().GetResult());
        Check(results, "pipe late reply", () => TransportChecks.LateReply().GetAwaiter().GetResult());
        Check(results, "pipe reply identity", () => TransportChecks.ReplyId().GetAwaiter().GetResult());
        Check(results, "pipe close during request", () => TransportChecks.CloseDuringRequest().GetAwaiter().GetResult());
        Check(results, "pipe server shutdown", () => TransportChecks.ServerShutdown().GetAwaiter().GetResult());
        Check(results, "pipe access control", () => TransportChecks.PipeAccess().GetAwaiter().GetResult());
        Check(results, "pipe connect cancellation", () =>
        {
            using var cancel = new CancellationTokenSource(100);
            try
            {
                using var client = JsonPipeClient.TryConnectAsync($"anode-selftest-{Guid.NewGuid():N}", 5000, cancel.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                throw new InvalidOperationException("connection attempt ignored cancellation");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            return "startup connection attempts can be interrupted by stop";
        });
        Check(results, "launch arguments", TransportChecks.LaunchArguments);
        Check(results, "Steam launch guard", DiagnosticsChecks.SteamLaunchGuard);
        Check(results, "desktop references", DesktopChecks.References);
        Check(results, "desktop dispatch", () => DesktopChecks.Dispatch().GetAwaiter().GetResult());
        Check(results, "desktop worker deadline", () => DesktopChecks.WorkerTimeout().GetAwaiter().GetResult());
        Check(results, "desktop report escaping", DesktopChecks.Presentation);
        Check(results, "execution output", DevelopmentChecks.Output);
        Check(results, "execution jobs", () => DevelopmentChecks.Jobs().GetAwaiter().GetResult());
        Check(results, "cancelled execution", () => DevelopmentChecks.CancelledStart().GetAwaiter().GetResult());
        Check(results, "malformed pipe messages", () => TransportChecks.MalformedMessages().GetAwaiter().GetResult());
        Check(results, "agent ownership", () => AgentChecks.Ownership().GetAwaiter().GetResult());
        Check(results, "agent queue and disconnect", () => AgentChecks.QueueAndDisconnect().GetAwaiter().GetResult());
        Check(results, "MCP ownership", () => AgentChecks.McpOwnership().GetAwaiter().GetResult());
        Check(results, "agent startup and Stop", () => AgentChecks.StartupAndStop().GetAwaiter().GetResult());
        Check(results, "UI condition waits", () => DevelopmentChecks.Waits().GetAwaiter().GetResult());
        Check(results, "development validation", DevelopmentChecks.Validation);
        Check(results, "hidden viewer startup", DevelopmentChecks.HiddenViewer);
        Check(results, "viewer pointer guard", DiagnosticsChecks.ViewerPointerGuard);
        Check(results, "RDP extended settings", DiagnosticsChecks.RdpSettings);
        Check(results, "RDP event callbacks", DiagnosticsChecks.RdpCallbacks);
        Check(results, "viewer sign-in", () => DiagnosticsChecks.ViewerSignIn().GetAwaiter().GetResult());
        Check(results, "RDP handshake diagnostics", DiagnosticsChecks.RdpHandshakeFailure);
        Check(results, "RDP listener probe", () => DiagnosticsChecks.Listener().GetAwaiter().GetResult());
        Check(results, "setup listener repair", DiagnosticsChecks.SetupListener);
        Check(results, "startup disconnect", () => DiagnosticsChecks.StartupFailure().GetAwaiter().GetResult());
        Check(results, "seat cleanup", () => DiagnosticsChecks.SeatCleanup().GetAwaiter().GetResult());
        Check(results, "startup status polling", () => DiagnosticsChecks.StartupPolling().GetAwaiter().GetResult());
        Check(results, "log diagnostics", () => DiagnosticsChecks.Logging().GetAwaiter().GetResult());
        Check(results, "MCP stdio", () => TransportChecks.McpStdio().GetAwaiter().GetResult());
        Check(results, "MCP validation", () => McpChecks.Validation().GetAwaiter().GetResult());
        Check(results, "MCP agent discovery", () => McpChecks.Discovery().GetAwaiter().GetResult());
        Check(results, "MCP stop and cancellation", () => McpChecks.StopAndCancellation().GetAwaiter().GetResult());
        Check(results, "MCP disconnect", () => McpChecks.Disconnect().GetAwaiter().GetResult());
        Check(results, "MCP busy daemon", () => McpChecks.BusyDaemon().GetAwaiter().GetResult());
        Check(results, "MCP command ordering", () => McpChecks.OrderedCommands().GetAwaiter().GetResult());
        Check(results, "seat identity guard", () =>
        {
            JsonObject Identity(int? child, int? parent) => JsonLine.Ok(new JsonObject
            {
                ["session"] = child, ["parentSession"] = parent
            });
            if (!Seat.SeatHost.MatchesSeat(3, Identity(3, 1)))
                throw new InvalidOperationException("the daemon's child session was rejected");
            foreach (var identity in new JsonObject?[] { null, JsonLine.Fail("unavailable"), Identity(null, 1), Identity(2, 1), Identity(3, 3), Identity(3, null), Identity(3, 0) })
                if (Seat.SeatHost.MatchesSeat(3, identity))
                    throw new InvalidOperationException("an unverified or parent session was accepted");
            if (Seat.SeatHost.MatchesSeat(0, Identity(0, 1)) || Seat.SeatHost.MatchesSeat(uint.MaxValue, Identity(-1, 1)))
                throw new InvalidOperationException("an invalid session id was accepted");
            return "only the child session identified by a different parent session is accepted";
        });

        if (includeEnvironment) Check(results, "task scheduler logging", DiagnosticsChecks.ScheduledLogging);

        if (includeGamepad)
        {
            Check(results, "virtual gamepad", () =>
            {
                using var pads = new GamepadManager();
                pads.Attach(0);
                try
                {
                    pads.Apply(0, new JsonObject
                    {
                        ["buttons"] = new JsonObject { ["a"] = true },
                        ["axes"] = new JsonObject { ["lx"] = 0.5 },
                        ["triggers"] = new JsonObject { ["rt"] = 1.0 }
                    });
                    pads.Reset(0);
                    pads.Tap(0, "b", 30);
                }
                finally { pads.Detach(0); }
                return "attached an Xbox 360 pad, moved a stick, tapped a button, detached";
            });
        }

        Check(results, "child-session API", () =>
        {
            bool enabled = ChildSession.IsFeatureEnabledSafe();
            uint? existing = ChildSession.TryGetId();
            return $"readable; feature {(enabled ? "enabled" : "disabled")}, current child session {(existing?.ToString() ?? "none")}";
        });

        Console.WriteLine("Anode self-test\n");
        foreach (var (name, ok, detail) in results)
            Console.WriteLine($"  [{(ok ? "ok  " : "FAIL")}] {name,-26} {detail}");

        int failures = results.Count(r => !r.Ok);
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All local checks passed. `anode doctor` covers the machine settings a seat still needs."
            : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(List<(string, bool, string)> results, string name, Func<string> body)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string detail = body();
            results.Add((name, true, $"{detail} ({stopwatch.ElapsedMilliseconds} ms)"));
        }
        catch (Exception ex)
        {
            Log.Error($"self-test '{name}' failed", ex);
            results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }
}
