using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Anode.Core.Input;
using Anode.Core.Session;

namespace Anode.Cli;

/// <summary>
/// The two halves of scripts/test-pointer-isolation.ps1. The probe moves the seat's
/// cursor the way an SDL game does and refuses to run anywhere but the seat. The watcher
/// samples the pointer of the session it runs in.
///
/// The Remote Desktop control only tries to move the real pointer while that pointer is
/// over the viewer's rectangle, shown or hidden. A run with the pointer elsewhere passes
/// on an unguarded daemon too, so the watcher does not start until the pointer is inside.
/// </summary>
internal static class PointerIsolation
{
    /// <summary>Further than one report of a flicked mouse travels; the probe's corners are much further apart.</summary>
    private const int JumpPixels = 120;

    public static int Probe(string[] args)
    {
        try
        {
            Seat.SeatHost.VerifyCurrentSession();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var options = new Args(args);
            string report = ReportPath(options);
            int moves = Math.Clamp(options.Int("moves") ?? 6, 1, 40);
            int interval = Math.Clamp(options.Int("interval") ?? 400, 50, 5000);

            var screen = InputInjector.ScreenSize();
            Point original = InputInjector.CursorPosition();
            Point[] corners = { new(screen.Width / 5, screen.Height / 5), new(screen.Width * 4 / 5, screen.Height * 4 / 5) };
            var log = new JsonArray();
            JsonObject Move(Point target)
            {
                DateTime at = DateTime.UtcNow;
                bool ok = SetCursorPos(target.X, target.Y);
                return new JsonObject { ["utc"] = at.ToString("O"), ["x"] = target.X, ["y"] = target.Y, ["ok"] = ok };
            }
            for (int i = 0; i < moves; i++)
            {
                log.Add(Move(corners[i % 2]));
                Thread.Sleep(interval);
            }
            // Games restore the cursor when they leave relative mode; so does the probe.
            log.Add(Move(original));

            Save(report, new JsonObject
            {
                ["session"] = ChildSession.CurrentSessionId(),
                ["screen"] = new JsonObject { ["width"] = screen.Width, ["height"] = screen.Height },
                ["moves"] = log
            });
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    }

    public static int Watch(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var options = new Args(args);
            string report = ReportPath(options);
            int duration = Math.Clamp(options.Int("duration") ?? 8000, 500, 120_000);
            int wait = Math.Clamp(options.Int("wait") ?? 60_000, 0, 600_000);
            int[] edges = (options.Value("rect") ?? "").Split(',').Select(part => int.TryParse(part, out int n) ? n : -1).ToArray();
            if (edges.Length != 4 || edges[2] < 200 || edges[3] < 200)
                throw new ArgumentException("--rect X,Y,WIDTH,HEIGHT must describe the viewer on this desktop.");
            var viewer = new Rectangle(edges[0], edges[1], edges[2], edges[3]);
            // Well inside, so a drifting hand does not leave the rectangle mid-run.
            var target = Rectangle.Inflate(viewer, -viewer.Width / 8, -viewer.Height / 8);

            bool placed = false;
            var waiting = Stopwatch.StartNew();
            Point start;
            while (!GetCursorPos(out start) || !target.Contains(start))
            {
                if (waiting.ElapsedMilliseconds > wait)
                {
                    Save(report, new JsonObject { ["session"] = ChildSession.CurrentSessionId(), ["ready"] = false });
                    return 4;
                }
                // Only on explicit request: this test exists because nothing should move this pointer.
                if (options.Flag("place")) placed = SetCursorPos(viewer.X + viewer.Width / 2, viewer.Y + viewer.Height / 2);
                Thread.Sleep(50);
            }
            File.WriteAllText(report + ".ready", "");

            var jumps = new JsonArray();
            int samples = 0, unreadable = 0;
            Point last = start;
            bool known = true;
            timeBeginPeriod(1);
            try
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < duration)
                {
                    Thread.Sleep(1);
                    // Fails while a secure desktop (UAC, lock screen) owns the input.
                    if (!GetCursorPos(out Point now)) { unreadable++; known = false; continue; }
                    double distance = Math.Sqrt(Math.Pow(now.X - last.X, 2) + Math.Pow(now.Y - last.Y, 2));
                    if (known && distance >= JumpPixels && jumps.Count < 200)
                        jumps.Add(new JsonObject
                        {
                            ["utc"] = DateTime.UtcNow.ToString("O"), ["fromX"] = last.X, ["fromY"] = last.Y,
                            ["toX"] = now.X, ["toY"] = now.Y, ["pixels"] = Math.Round(distance, 1)
                        });
                    samples++;
                    last = now; known = true;
                }
            }
            finally { timeEndPeriod(1); }

            Save(report, new JsonObject
            {
                ["session"] = ChildSession.CurrentSessionId(), ["ready"] = true, ["placed"] = placed,
                ["samples"] = samples, ["unreadable"] = unreadable, ["durationMs"] = duration,
                ["startX"] = start.X, ["startY"] = start.Y, ["endX"] = last.X, ["endY"] = last.Y, ["jumps"] = jumps
            });
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
    }

    private static string ReportPath(Args options)
    {
        string report = options.Value("report") ?? throw new ArgumentException("--report PATH is required.");
        if (!Path.IsPathFullyQualified(report) || !Directory.Exists(Path.GetDirectoryName(report)))
            throw new ArgumentException("--report must be an absolute path in an existing directory.");
        return report;
    }

    private static void Save(string report, JsonObject content)
    {
        File.WriteAllText(report + ".tmp", content.ToJsonString());
        File.Move(report + ".tmp", report, overwrite: true);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);
}
