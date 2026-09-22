using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Core.Processes;
using Anode.Mcp;

namespace Anode.Cli;

internal static class DevelopmentChecks
{
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    public static string HiddenViewer()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (var window = new Daemon.SeatWindow(new Daemon.SeatOptions()))
                {
                    int shown = 0;
                    window.Shown += (_, _) => shown++;
                    window.CreateHiddenViewer();
                    Application.DoEvents();
                    Require(window.IsHandleCreated && window.Viewer.IsHandleCreated && !window.Visible && shown == 0, "hidden startup showed the viewer or omitted its native control");
                    var toolbar = window.Controls.OfType<ToolStrip>().Single(s => s.Name == "SeatToolbar");
                    var status = window.Controls.OfType<StatusStrip>().Single();
                    var details = (TextBox)window.Controls.Find("SeatDetails", true).Single();
                    Require(window.ViewOnly && status.Items["InputMode"]?.Text == "View only", "initial input mode is unclear");
                    window.ViewOnly = false;
                    Require(status.Items["InputMode"]?.Text == "You have control"
                        && toolbar.Items["ControlSeat"]?.AccessibleName == "Release control", "taking control did not update visible/accessible state");
                    window.ViewOnly = true;
                    string failure = "Windows could not sign in. " + new string('x', 1200) + " Run anode doctor.";
                    window.SetStatus(failure);
                    window.SetHeadline("logon-error");
                    window.SetSeatInfo(42, false);
                    Require(details.ReadOnly && details.Text.Contains(failure) && details.Text.Contains("Session 42")
                        && details.Text.Contains("View only") && details.Text.Contains("Emergency"), "full status or recovery information was lost");
                    Require(toolbar.Items["SeatState"]?.Text == "Sign-in failed", "viewer exposed a protocol state instead of a readable label");
                    var placeholder = (Daemon.SeatPlaceholder)window.Controls.Find("SeatPlaceholder", false).Single();
                    Require(!window.SeatPictureLive && placeholder.Heading == "Sign-in failed" && placeholder.AccessibleDescription == failure,
                        "the connecting screen did not describe the seat's state");
                    window.SetSeatPictureLive(true);
                    Require(window.SeatPictureLive, "a connected seat stayed behind the connecting screen");
                    window.SetSeatPictureLive(false);
                    var originalBounds = window.Bounds;
                    window.ToggleFullScreen();
                    toolbar.PerformLayout();
                    // WS_VISIBLE on the child itself is testable without showing its parent.
                    Require((Core.Native.Native.GetWindowLong(toolbar.Handle, -16) & 0x10000000) != 0,
                        "full screen hid emergency controls");
                    Require(toolbar.Items["FullScreen"]?.Text == "Exit full screen", "full screen has no visible exit action");
                    window.ToggleFullScreen();
                    Require(window.Bounds == originalBounds && window.FormBorderStyle == FormBorderStyle.Sizable,
                        "leaving full screen lost the window bounds or border");
                    window.Width = window.MinimumSize.Width;
                    toolbar.PerformLayout();
                    foreach (string name in new[] { "StopSeat", "ControlSeat", "FullScreen" })
                    {
                        var button = toolbar.Items[name]!;
                        Require(button.Placement == ToolStripItemPlacement.Main && button.Bounds.Right <= toolbar.Width,
                            name + " disappeared at minimum width");
                    }
                    int stops = 0;
                    window.StopSeatRequested += () => stops++;
                    toolbar.Items["StopSeat"]!.PerformClick();
                    Require(stops == 1, "the Stop button did not dispatch immediately");
                    Require(!window.Visible && shown == 0, "viewer checks showed a window");
                }
                finished.TrySetResult();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return "hidden RDP initialization, input labels, connecting screen, full diagnostics, full-screen Stop/exit and narrow layout; no connection or shown form";
    }
    public static string Output()
    {
        var output = new ExecutionOutput(12);
        output.Append("stdout", "abcd"); output.Append("stderr", "ERROR");
        var first = output.Read(null, 6);
        Require(first.Str("stdout") == "abcd" && first.Str("stderr") == "ER" && first.Bool("hasMoreOutput") == true, "stream pagination lost output");
        var next = output.Read(first.Str("cursor"), 12);
        Require(next.Str("stderr") == "ROR" && next.Bool("hasMoreOutput") == false, "cursor duplicated output");
        output.Append("stdout", new string('x', 20));
        var truncated = output.Read(next.Str("cursor"), 12);
        Require(truncated.Bool("outputTruncated") == true && truncated.Str("stdout")?.Length == 12, "overflow was not bounded and explicit");
        try { output.Read("9999", 10); throw new InvalidOperationException("future cursor accepted"); } catch (ArgumentException) { }
        var unicode = new ExecutionOutput();
        unicode.Append("stdout", "a\U0001F680b");
        unicode.Append("stderr", "\U00010437z");
        var pages = new List<JsonObject>();
        string? cursor = null;
        foreach (int maximum in new[] { 2, 1, 1, 1 })
        {
            var page = JsonLine.Parse(JsonLine.Serialize(unicode.Read(cursor, maximum)))!;
            pages.Add(page);
            cursor = page.Str("cursor");
        }
        Require(pages[0].Str("stdout") == "a\U0001F680" && pages[1].Str("stdout") == "b"
            && pages[2].Str("stderr") == "\U00010437" && pages[3].Str("stderr") == "z" && pages[^1].Bool("hasMoreOutput") == false,
            "pagination split a Unicode character or stopped advancing at a one-character limit");
        var unicodeTail = new ExecutionOutput(3);
        unicodeTail.Append("stdout", "ab\U0001F680xy");
        var tail = JsonLine.Parse(JsonLine.Serialize(unicodeTail.Read(null, 3)))!;
        Require(tail.Str("stdout") == "xy" && tail.Bool("outputTruncated") == true, "retention kept half a Unicode character");
        return "separate streams, incremental cursors, Unicode-safe pages/retention, output limits and lost-data reporting";
    }
    public static async Task<string> Jobs()
    {
        string prefix = new('x', 4095);
        using (var reader = new StringReader(prefix + "\U0001F680tail"))
        {
            var buffered = new ExecutionOutput();
            await ExecutionJobs.DrainAsync(reader, "stdout", buffered);
            var page = JsonLine.Parse(JsonLine.Serialize(buffered.Read(null, 4096)))!;
            Require(page.Str("stdout") == prefix + "\U0001F680" && buffered.Read(page.Str("cursor"), 4).Str("stdout") == "tail",
                "stream buffer split a Unicode character across output chunks");
        }
        ProcessStartInfo Worker(string code)
        {
            var info = new ProcessStartInfo("powershell.exe");
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$null = [Console]::In.ReadLine(); " + code)) }) info.ArgumentList.Add(arg);
            return info;
        }
        using (var jobs = new ExecutionJobs(() => Worker("[Console]::Out.Write('hello'); [Console]::Error.Write('problem'); exit 7")))
        {
            var done = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 10000 }, "test-agent");
            Require(done.Bool("finished") == true && done.Int("exitCode") == 7 && done.Str("stdout") == "hello" && done.Str("stderr") == "problem", "exit code or streams missing: " + done);
            var listed = await jobs.ReadAsync(new JsonObject { ["action"] = "list" }, "test-agent");
            Require(listed["jobs"] is JsonArray { Count: 1 }, "interrupted-call job recovery unavailable");
        }
        // Simulate a non-UTF-8 Windows console; the execution protocol still promises UTF-8.
        using (var jobs = new ExecutionJobs(() =>
        {
            var info = Worker("$bytes = [System.Text.Encoding]::UTF8.GetBytes('caf\u00e9 \u6771\u4eac \U0001F680'); [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length); [Console]::OpenStandardError().Write($bytes, 0, $bytes.Length)");
            info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.Latin1;
            return info;
        }))
        {
            var done = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 10000 }, "test-agent");
            Require(done.Bool("finished") == true && done.Int("exitCode") == 0
                && done.Str("stdout") == "caf\u00e9 \u6771\u4eac \U0001F680" && done.Str("stderr") == "caf\u00e9 \u6771\u4eac \U0001F680",
                "UTF-8 output depends on the host console encoding: " + done);
        }
        using (var jobs = new ExecutionJobs(() => Worker("Start-Sleep -Seconds 30")))
        {
            var done = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["executionTimeoutMs"] = 500, ["waitMs"] = 5000 }, "test-agent");
            Require(done.Str("state") == "timed_out" && done.Bool("finished") == true, "hard lifetime did not stop the worker");
        }
        using (var responseCancelled = new CancellationTokenSource())
        using (var jobs = new ExecutionJobs(() =>
        {
            responseCancelled.Cancel(); // The job is committed, but the client no longer waits for its reply.
            return Worker("[Console]::Out.Write('survived')");
        }))
        {
            bool rejected = false;
            try { await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 10000 }, "test-agent", responseCancelled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "the cancelled client call still waited for its command");
            var listed = await jobs.ReadAsync(new JsonObject { ["action"] = "list" }, "test-agent");
            Require(listed["jobs"] is JsonArray { Count: 1 }, "interrupted start was not recoverable or created more than one job");
            var recovered = await jobs.ReadAsync(new JsonObject { ["jobId"] = listed["jobs"]![0]!["jobId"]!.DeepClone(), ["waitMs"] = 10000 }, "test-agent");
            Require(recovered.Bool("finished") == true && recovered.Str("state") == "completed" && recovered.Str("stdout") == "survived",
                "client cancellation killed an already started command");
        }
        using (var jobs = new ExecutionJobs(() => Worker("$p = Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30")))
        {
            var started = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 2000 }, "test-agent");
            Require(int.TryParse(started.Str("stdout")?.Trim(), out int descendant), "descendant did not start");
            try
            {
                await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId"), ["action"] = "cancel", ["after"] = "999999" }, "test-agent");
                throw new InvalidOperationException("invalid cancellation cursor accepted");
            }
            catch (ArgumentException) { }
            Require((await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId") }, "test-agent")).Str("state") == "running", "invalid request cancelled a live job");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            foreach (string action in new[] { "read", "cancel" })
            {
                bool rejected = false;
                try { await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId"), ["action"] = action }, "test-agent", cancelled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Require(rejected, "a cancelled " + action + " request was still executed");
                Require((await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId") }, "test-agent")).Str("state") == "running",
                    "a cancelled client request stopped an existing job");
            }
            var stopped = await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId"), ["action"] = "cancel", ["waitMs"] = 5000 }, "test-agent");
            Require(stopped.Str("state") == "cancelled" && stopped.Bool("finished") == true, "job cancellation did not finish");
            try { using var child = Process.GetProcessById(descendant); Require(child.HasExited || child.WaitForExit(2000), "descendant survived job cancellation"); } catch (ArgumentException) { }
        }
        using (var jobs = new ExecutionJobs(() => Worker("Start-Sleep -Seconds 30")))
        {
            var a = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 0 }, "A");
            var b = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 0 }, "B");
            var listed = await jobs.ReadAsync(new JsonObject { ["action"] = "list" }, "A");
            Require(listed["jobs"] is JsonArray { Count: 1 } && listed["jobs"]![0]!["jobId"]!.GetValue<string>() == a.Str("jobId"), "job discovery leaked another agent's job");
            foreach (string action in new[] { "read", "cancel" })
            {
                bool refused = false;
                try { await jobs.ReadAsync(new JsonObject { ["jobId"] = b.Str("jobId"), ["action"] = action }, "A"); }
                catch (InvalidOperationException) { refused = true; }
                Require(refused, "another agent's job was readable or cancellable");
            }
            Require(jobs.CancelOwned("A") == 1, "owned cleanup missed a running job");
            Require((await jobs.ReadAsync(new JsonObject { ["jobId"] = a.Str("jobId"), ["waitMs"] = 5000 }, "A")).Str("state") == "cancelled", "owned job survived cleanup");
            Require((await jobs.ReadAsync(new JsonObject { ["jobId"] = b.Str("jobId") }, "B")).Str("state") == "running", "owned cleanup cancelled another agent's job");
            await jobs.ReadAsync(new JsonObject { ["jobId"] = b.Str("jobId"), ["action"] = "cancel", ["waitMs"] = 5000 }, "B");
        }
        return "UTF-8 streams/exit codes, recovery, hard timeouts, cancelled requests, owner-only discovery/read/cancel and cleanup of owned descendants";
    }
    public static async Task<string> CancelledStart()
    {
        int starts = 0;
        using var jobs = new ExecutionJobs(() => { starts++; throw new InvalidOperationException("cancelled request reached the worker"); });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool rejected = false;
        try { await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 0 }, "test-agent", cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && starts == 0, "an already cancelled execution request started a job");
        Require((await jobs.ReadAsync(new JsonObject { ["action"] = "list" }, "test-agent"))["jobs"] is JsonArray { Count: 0 },
            "a cancelled execution request left a job in history");
        return "already cancelled execution requests have no process or job-history side effects";
    }
    public static async Task<string> Waits()
    {
        int reads = 0;
        JsonObject Observation(bool enabled, bool truncated = false) => JsonLine.Ok(new JsonObject
        {
            ["truncated"] = truncated, ["warnings"] = new JsonArray(), ["snapshotId"] = "fresh",
            ["elements"] = new JsonArray(new JsonObject { ["automationId"] = "Save", ["enabled"] = enabled, ["name"] = "Ready" })
        });
        var args = new JsonObject { ["windowId"] = "test", ["automationId"] = "Save", ["state"] = "enabled", ["waitMs"] = 2000 };
        var result = await DesktopWait.RunAsync(args, (_, _) => Task.FromResult(Observation(++reads >= 2)), default);
        Require(result.Obj("result")?.Bool("matched") == true && reads == 2 && result.Obj("result")?.Str("snapshotId") == "fresh", "UI wait did not return the matching observation");
        args["automationId"] = "absent"; args["state"] = "missing"; args["waitMs"] = 0;
        result = await DesktopWait.RunAsync(args, (_, _) => Task.FromResult(Observation(true, true)), default);
        Require(result.Obj("result")?.Bool("matched") == false && result.Obj("result")?.Str("snapshotId") is null, "incomplete tree proved absence or returned an actionable timeout");
        Require(!result.Obj("result")!.Str("summary")!.Contains("Snapshot fresh"), "timeout summary advertised an actionable snapshot");
        args["waitMs"] = 100;
        using var cancel = new CancellationTokenSource(30);
        try { await DesktopWait.RunAsync(args, async (_, token) => { await Task.Delay(10000, token); return Observation(true); }, cancel.Token); throw new InvalidOperationException("wait ignored cancellation"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        return "fresh matching observations, honest timeouts, incomplete-tree refusal and cancellation";
    }
    public static string Validation()
    {
        foreach (var (tool, json) in new[] {
            ("seat_exec", "{\"path\":\"\"}"), ("seat_exec", "{\"path\":\"dotnet\",\"cwd\":\"relative\"}"),
            ("seat_exec", "{\"path\":\"dotnet\",\"waitMs\":10001}"), ("seat_exec", "{\"path\":\"dotnet\",\"env\":{\"bad=name\":\"x\"}}"),
            ("seat_job", "{}"), ("seat_job", "{\"action\":\"list\",\"jobId\":\"x\"}"), ("seat_job", "{\"jobId\":\"x\",\"after\":\"-1\"}"),
            ("seat_wait", "{\"windowId\":\"x\"}"), ("seat_wait", "{\"windowId\":\"x\",\"textContains\":\"text\",\"state\":\"missing\"}") })
            Require(Tools.ValidateArguments(tool, JsonNode.Parse(json)!.AsObject()) is not null, "invalid " + tool + " accepted");
        return "execution arguments, environment, cursors and UI predicates validated before dispatch";
    }
}
