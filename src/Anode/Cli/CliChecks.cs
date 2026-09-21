using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anode.Core.Bridge;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Cli;

/// <summary>
/// Command-line routing and wording, tested through pure functions. Nothing here connects to
/// a pipe or elevates, and the only process started is `anode mcp --help` with its input
/// closed: if help routing regressed, launching `anode kill --help` would sign out the live seat.
/// </summary>
internal static class CliChecks
{
    // Every public name the dispatcher in Cli.Run accepts, except the help spellings.
    private static readonly string[] Dispatched =
    {
        "version", "--version", "-v", "doctor", "configure", "guide", "rendering", "selftest", "self-test", "setup", "up",
        "start", "status", "lease", "kill", "stop", "quit", "show", "hide", "control", "run", "steam", "shot", "screenshot",
        "click", "move", "scroll", "key", "type", "ps", "gamepad", "pad", "windows", "inspect", "window", "element",
        "capabilities", "exec", "job", "jobs", "wait", "mcp"
    };

    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    public static string HelpRouting()
    {
        (string Command, string[] Arguments, bool Help)[] cases =
        {
            ("kill", new[] { "--help" }, true), ("stop", new[] { "-h" }, true), ("quit", new[] { "/?" }, true),
            ("setup", new[] { "--fps", "60", "--help" }, true), ("start", new[] { "--hidden", "-H" }, true),
            ("selftest", new[] { "--HELP" }, true), ("show", new[] { "-?" }, true), ("mcp", new[] { "--help" }, true),
            ("control", new[] { "take", "--help" }, true), ("click", new[] { "10", "20", "--help" }, true),
            ("run", new[] { "--help" }, true), ("run", new[] { "notepad", "--help" }, false),
            ("type", new[] { "-h" }, true), ("type", new[] { "hello", "--help" }, false), ("key", new[] { "/?" }, true),
            ("exec", new[] { "--", "x", "--help" }, false), ("exec", new[] { "--cwd", @"C:\x", "--help" }, true),
            ("exec", new[] { "--env", "--help", "--", "x" }, false),
            ("element", new[] { "s", "e", "set_value", "--value", "--help" }, false),
            ("windows", new[] { "--query", "-h" }, false), ("wait", new[] { "w", "--name", "--help" }, false),
            ("wait", new[] { "w", "--text", "/?" }, false), ("status", Array.Empty<string>(), false), ("mcp", Array.Empty<string>(), false),
            ("configure", new[] { "--help" }, true), ("configure", new[] { "codex", "-h" }, true), ("version", new[] { "--help" }, true)
        };
        foreach (var (command, rest, help) in cases)
            Require(Cli.AsksHelp(command, rest) == help,
                $"'{command} {string.Join(' ', rest)}' {(help ? "did not ask" : "asked")} for help");
        return $"{cases.Length} command lines; help never runs the command, and free text stays data";
    }

    /// <summary>
    /// MCP stdout carries only protocol messages, so `mcp --help` must answer on stderr. Input is
    /// closed at once: a regression that started the server would read end of input and exit.
    /// </summary>
    public static string McpHelp()
    {
        var info = new ProcessStartInfo(Env.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("mcp");
        info.ArgumentList.Add("--help");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start `anode mcp --help`");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();
            Require(process.WaitForExit(15_000), "mcp --help did not exit");
            Require(process.ExitCode == 0 && stdout.Result.Length == 0, $"mcp --help exited {process.ExitCode} or wrote to stdout");
            Require(stderr.Result.Contains("anode mcp", StringComparison.Ordinal), "mcp --help printed no help on stderr");
            return "help on stderr, nothing on stdout, exit 0";
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
        }
    }

    public static string Arguments()
    {
        string[][] accepted =
        {
            new[] { "setup" }, new[] { "setup", "--fps", "60", "--gpu" }, new[] { "setup", "--fps=60" }, new[] { "setup", "--undo" },
            new[] { "start", "--hidden", "--width", "1920", "--height", "1080", "--audio" },
            new[] { "up", "--state-dir", @"C:\Anode logs", "--hidden", "--state-dir", @"C:\x" }, new[] { "start", "-hidden" },
            new[] { "selftest", "--quick" }, new[] { "self-test", "--no-gamepad", "--no-scheduler" }, new[] { "kill" }, new[] { "stop" },
            new[] { "control" }, new[] { "control", "take" }, new[] { "doctor", "--json" }, new[] { "status", "--json" },
            new[] { "rendering", "--restore" }, new[] { "version" }, new[] { "click" }, new[] { "click", "10", "20", "--double", "--right" },
            new[] { "click", "-5", "10" }, new[] { "move", "1", "2" }, new[] { "scroll" }, new[] { "scroll", "-3" },
            new[] { "key", "ctrl+shift+esc" }, new[] { "type", "--literal", "text" }, new[] { "run", "notepad", "--anything" },
            new[] { "ps" }, new[] { "ps", "--all" }, new[] { "ps", "kill", "notepad" }, new[] { "steam" }, new[] { "steam", "status" },
            new[] { "steam", "--force", "220" }, new[] { "gamepad" }, new[] { "pad", "attach", "--slot", "1" },
            new[] { "gamepad", "stick", "left", "0.5", "-0.25" }, new[] { "gamepad", "tap", "a", "--ms", "80" },
            new[] { "windows", "--query", "--help", "--json" }, new[] { "inspect", "--json", "w_1", "--max-depth", "20", "--offscreen" },
            new[] { "window", "w_1", "move", "--x", "-10", "--y=0", "--width", "800", "--height", "600" },
            new[] { "element", "s", "e", "set_value", "--value", "--help" }, new[] { "shot", "out.png", "--width", "1000", "--jpeg" },
            new[] { "exec", "--whatever" }, new[] { "nosuch", "--anything" }
        };
        foreach (var line in accepted)
        {
            try { Cli.CheckArguments(line[0], line[1..]); }
            catch (UsageException ex) { throw new InvalidOperationException($"'{string.Join(' ', line)}' was rejected: {ex.Message}"); }
        }

        string[][] rejected =
        {
            new[] { "setup", "--dry-run" }, new[] { "setup", "undo" }, new[] { "setup", "--fps", "30" }, new[] { "setup", "--state-dir", @"C:\x" },
            new[] { "start", "--headless" }, new[] { "start", "--width", "wide" }, new[] { "start", "--hidden=yes" },
            new[] { "start", "-width", "5" }, new[] { "up", "--state-dir" }, new[] { "start", "--sign-in", "--hidden" },
            new[] { "selftest", "--full" }, new[] { "kill", "now" }, new[] { "stop", "--force" }, new[] { "quit", "-y" },
            new[] { "show", "viewer" }, new[] { "hide", "--all" }, new[] { "control", "maybe" }, new[] { "control", "take", "give" },
            new[] { "click", "500", "--double" }, new[] { "click", "1", "2", "3" }, new[] { "click", "a", "b" },
            new[] { "click", "1", "2", "--right", "--middle" }, new[] { "click", "1", "2", "--doubel" }, new[] { "move", "1" },
            new[] { "move", "1", "y" }, new[] { "scroll", "down" }, new[] { "scroll", "3x" }, new[] { "scroll", "1", "2" },
            new[] { "key" }, new[] { "key", "ctrl", "+", "c" }, new[] { "type" }, new[] { "run" }, new[] { "ps", "kill" },
            new[] { "ps", "list" }, new[] { "steam", "abc" }, new[] { "steam", "status", "--force" }, new[] { "gamepad", "bogus" },
            new[] { "gamepad", "stick", "left", "x", "0" }, new[] { "gamepad", "stick", "up", "0", "0" }, new[] { "gamepad", "stick", "left", "2", "0" },
            new[] { "gamepad", "tap" }, new[] { "gamepad", "attach", "--slot", "9" }, new[] { "gamepad", "attach", "--ms", "80" },
            new[] { "inspect", "w", "--max-depth" }, new[] { "inspect", "w", "extra" }, new[] { "windows", "--pid", "abc" },
            new[] { "doctor", "--verbose" }, new[] { "status", "--jsn" }, new[] { "version", "--json" }, new[] { "shot", "a.png", "b.png" }
        };
        foreach (var line in rejected)
        {
            bool refused = false;
            try { Cli.CheckArguments(line[0], line[1..]); }
            catch (UsageException) { refused = true; }
            Require(refused, $"'{string.Join(' ', line)}' was accepted");
        }

        // scripts/test-install.ps1 expects an unknown client to fail before any registration.
        Require(Cli.ConfigureArguments(Array.Empty<string>()) == ("auto", false)
            && Cli.ConfigureArguments(new[] { "codex", "--no-skill" }) == ("codex", true), "configure lost its client or skill choice");
        foreach (var line in new[] { new[] { "invalid-client" }, new[] { "codex", "claude" }, new[] { "--no-skill", "--no-skill" } })
        {
            bool refused = false;
            try { Cli.ConfigureArguments(line); }
            catch (UsageException) { refused = true; }
            Require(refused, $"'configure {string.Join(' ', line)}' was accepted");
        }

        Require(Cli.CliError("arguments.waitMs must be at most 10000.") == "--wait must be at most 10000."
            && Cli.CliError("arguments.windowId is required.") == "<windowId> is required."
            && Cli.CliError("arguments.cancelJobs is only allowed for release.") == "--cancel-jobs is only allowed for release."
            && Cli.CliError("Provide both x and y.") == "Provide both x and y.", "tool validation kept MCP argument names");
        return $"{accepted.Length} accepted and {rejected.Length} rejected before any side effect";
    }

    public static string HelpText()
    {
        string full = Cli.FullHelp();
        Require(full.StartsWith("anode - a background Windows desktop for AI agents\n", StringComparison.Ordinal), "help lost its header");
        // Wrapping may break a phrase across lines.
        string flat = Regex.Replace(full, @"\s+", " ");
        foreach (string text in new[] { "not a security sandbox", "anode start --hidden", "anode doctor", "anode configure",
            "report seat capture and known input blockers", "anode selftest --quick", "ignores leases", "anode help <command>",
            "Agents: anode guide", "| Out-Host", "Exit codes:", "1223", "seat_drag is MCP-only", "seat_observe = inspect",
            Links.Readme, Links.Troubleshooting, @"C:\project " })
            Require(flat.Contains(text, StringComparison.Ordinal), $"full help lacks '{text.Trim()}'");
        Require(full.Contains("\n  Start here\n", StringComparison.Ordinal) && full.Contains("\n  Diagnose\n", StringComparison.Ordinal),
            "full help lacks its Start here or Diagnose group");
        Require(!full.Contains(@"C:\\", StringComparison.Ordinal), "full help prints a doubled backslash");
        Require(!full.Contains('\t') && full.Split('\n').All(line => line.Length <= 100), "a help line is wider than 100 columns");
        Require(full.IndexOf("anode doctor", StringComparison.Ordinal) < full.IndexOf("  Diagnose", StringComparison.Ordinal)
            && full.IndexOf("anode start --hidden", StringComparison.Ordinal) < full.IndexOf("  Diagnose", StringComparison.Ordinal),
            "Start here does not come first");

        foreach (string command in Dispatched)
        {
            Require(Cli.TopicHelp(command) is { Length: > 0 } topic && topic.Split('\n').All(line => line.Length <= 100), $"'{command}' has no help");
            Require(Cli.Usage(command) is { } usage && usage.StartsWith("usage: anode ", StringComparison.Ordinal), $"'{command}' has no usage");
            string name = command.TrimStart('-');
            Require(name.Length == 1 || full.Contains(name, StringComparison.Ordinal), $"full help never mentions '{command}'");
        }
        Require(Cli.TopicHelp("nosuch") is null && Cli.Usage("nosuch") is null, "an unknown topic had help");
        Require(Cli.TopicHelp("start")!.Contains("--no-background-rendering", StringComparison.Ordinal), "start help lacks its options");
        foreach (string option in "hidden width height scale no-scaling audio clipboard winkeys control sign-in no-background-rendering state-dir keep".Split(' '))
            Require(Cli.TopicHelp("up")!.Contains("--" + option, StringComparison.Ordinal), $"up help lacks --{option}");
        Require(Regex.IsMatch(Cli.VersionLine(), @"^anode \d+\.\d+\.\d+$"), "version is not one 'anode x.y.z' line");

        (string Name, string? Expected)[] suggestions =
        {
            ("observe", "inspect"), ("seat_observe", "inspect"), ("seat_screenshot", "screenshot"), ("seat_processes", "ps"),
            ("seat_kill_process", "ps kill"), ("seat_stop", "stop"), ("seat_hide", "hide"), ("steam_launch", "steam"),
            ("gamepad_tap", "gamepad tap"), ("anode_guide", "guide"), ("seat_job", "job"), ("stauts", "status"),
            ("docter", "doctor"), ("zzzzzz", null)
        };
        foreach (var (name, expected) in suggestions)
            Require(Cli.Suggestion(name) == expected, $"'{name}' suggested '{Cli.Suggestion(name)}', expected '{expected}'");

        var stopped = Cli.StoppedStatus("agent-1");
        Require(stopped.Str("state") == "stopped" && stopped.Bool("daemonRunning") == false && stopped.Str("agentId") == "agent-1"
            && stopped.ContainsKey("ownerAgentId") && stopped["ownerAgentId"] is null && stopped.Str("summary") == Cli.NotRunningText
            && stopped.Count == 5, "stopped status differs from the MCP shape");

        var ready = Cli.DoctorJson(new[] { new Check("a", CheckLevel.Pass, "x"), new Check("b", CheckLevel.Warn, "y", "fix") });
        var blocked = Cli.DoctorJson(new[] { new Check("a", CheckLevel.Fail, "x", "Run `anode setup`.") });
        Require(ready.Bool("ready") == true && ready.Str("version") == Cli.VersionText() && ready["checks"] is JsonArray { Count: 2 }
            && blocked.Bool("ready") == false && blocked["checks"]?[0]?["state"]?.GetValue<string>() == "fail", "doctor JSON is incomplete");
        return $"one table covers {Dispatched.Length} command names; wording, suggestions and JSON shapes hold";
    }
}
