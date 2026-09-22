using System.Diagnostics;
using System.Windows.Forms;
using Anode.Core.Launch;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Cli;

internal static partial class Cli
{
    /// <summary>
    /// What the Start menu shortcut and a double-click from Explorer do: show the viewer,
    /// starting Anode first when it is not running. There is no console to print into,
    /// so everything that needs words is a dialog.
    /// </summary>
    private static int Open()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        // The user's click gave this process the foreground. Pass it on, or Windows keeps
        // the viewer behind whatever was active before the Start menu opened.
        Core.Native.Native.AllowSetForegroundWindow(Core.Native.Native.ASFW_ANY);
        try
        {
            using (var running = Connect(autoStart: false).GetAwaiter().GetResult())
            {
                if (running is not null)
                {
                    Log.Info("opened from Windows: showing the running viewer");
                    RequestAsync(running, "seat.show").GetAwaiter().GetResult();
                    // Opening Anode after someone pressed Stop seat brings the seat back, as it
                    // would when Anode was not running. A running seat answers immediately.
                    RequestAsync(running, "seat.start", timeoutMs: 180_000).GetAwaiter().GetResult();
                    return 0;
                }
            }

            var failures = Preconditions.Run().Where(c => c.State == CheckLevel.Fail).ToArray();
            if (failures.Length > 0 && !OfferSetup(failures)) return 3;

            Log.Info("opened from Windows: starting Anode with its viewer");
            DaemonLauncher.Launch(Array.Empty<string>());
            // Wait on the thread pool: a setup dialog may have left a UI synchronization context
            // on this thread, and blocking on a continuation posted to it would never finish.
            using var client = Task.Run(() => WaitForDaemon(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
            if (client is null)
            {
                Tell(TaskDialogIcon.Error, "Anode did not start",
                    $"Anode did not answer within 30 seconds. Its log may say why:\n{Env.LogPath}",
                    "Open troubleshooting", Links.Troubleshooting + "#the-seat-will-not-come-up");
                return 1;
            }
            // The viewer first appears without taking focus, so an agent's start cannot steal
            // keystrokes. This start came from a click, so bring it forward.
            Task.Run(() => RequestAsync(client, "seat.show")).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("opening Anode from Windows failed", ex);
            Tell(TaskDialogIcon.Error, "Anode could not open", ex.Message,
                "Open troubleshooting", Links.Troubleshooting);
            return 1;
        }
    }

    /// <summary>
    /// Explains what keeps this PC from hosting a seat and, when `anode setup` is the whole
    /// fix, offers to run it. Returns true once nothing blocks a seat any more.
    /// </summary>
    private static bool OfferSetup(IReadOnlyList<Check> failures)
    {
        string problems = string.Join("\n", failures.Select(c => $"• {c.Name}: {c.Detail}"));
        if (!failures.All(c => c.Fix?.Contains("anode setup", StringComparison.Ordinal) == true))
        {
            Tell(TaskDialogIcon.Warning, "This PC cannot run an Anode seat", problems,
                "Open the requirements", Links.Install + "#requirements");
            return false;
        }

        var setUp = new TaskDialogButton("Set up now") { ShowShieldIcon = true };
        var page = new TaskDialogPage
        {
            Caption = "Anode",
            Heading = "Anode needs a one-time setup",
            Text = "Setup turns on Windows child sessions and the local Remote Desktop host that Anode's "
                + "background desktop runs in. It opens no firewall ports, and Windows asks for "
                + "administrator approval.\n\n" + problems,
            Icon = TaskDialogIcon.Information,
            Buttons = { setUp, TaskDialogButton.Cancel },
            DefaultButton = setUp,
            Footnote = new TaskDialogFootnote($"<a href=\"{Links.Security}#what-anode-setup-changes\">What setup changes and how to undo it</a>"),
            EnableLinks = true
        };
        page.LinkClicked += (_, e) => OpenLink(e.LinkHref);
        if (TaskDialog.ShowDialog(page) != setUp) return false;

        Env.ResolveStateDirectory();
        int code = Core.Session.Setup.RelaunchElevated("__apply-setup --state-dir " + DaemonLauncher.Quote(Env.StateDirectory));
        if (code == 1223) return false; // approval declined; nothing changed
        var remaining = Preconditions.Run().Where(c => c.State == CheckLevel.Fail).ToArray();
        if (remaining.Length == 0) return true;
        Tell(TaskDialogIcon.Error, "Setup did not finish",
            string.Join("\n", remaining.Select(c => $"• {c.Name}: {c.Detail}")) + $"\n\nDetails are in {Env.LogPath}",
            "Open troubleshooting", Links.Troubleshooting + "#the-seat-will-not-come-up");
        return false;
    }

    private static void Tell(TaskDialogIcon icon, string heading, string text, string action, string link)
    {
        var open = new TaskDialogButton(action);
        var page = new TaskDialogPage
        {
            Caption = "Anode", Heading = heading, Text = text, Icon = icon,
            Buttons = { open, TaskDialogButton.Close }, DefaultButton = open
        };
        if (TaskDialog.ShowDialog(page) == open) OpenLink(link);
    }

    private static void OpenLink(string link)
    {
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warn($"could not open {link}: {ex.Message}"); }
    }
}
