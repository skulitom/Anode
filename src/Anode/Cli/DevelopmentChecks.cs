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
                }
                finished.TrySetResult();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return "native RDP control initialized without showing a form or connecting";
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
        return "separate streams, incremental cursors, output limits and lost-data reporting";
    }
    public static async Task<string> Jobs()
    {
        ProcessStartInfo Worker(string code)
        {
            var info = new ProcessStartInfo("powershell.exe");
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$null = [Console]::In.ReadLine(); " + code)) }) info.ArgumentList.Add(arg);
            return info;
        }
        using (var jobs = new ExecutionJobs(() => Worker("[Console]::Out.Write('hello'); [Console]::Error.Write('problem'); exit 7")))
        {
            var done = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 10000 });
            Require(done.Bool("finished") == true && done.Int("exitCode") == 7 && done.Str("stdout") == "hello" && done.Str("stderr") == "problem", "exit code or streams missing: " + done);
            var listed = await jobs.ReadAsync(new JsonObject { ["action"] = "list" });
            Require(listed["jobs"] is JsonArray { Count: 1 }, "interrupted-call job recovery unavailable");
        }
        using (var jobs = new ExecutionJobs(() => Worker("Start-Sleep -Seconds 30")))
        {
            var done = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["executionTimeoutMs"] = 500, ["waitMs"] = 5000 });
            Require(done.Str("state") == "timed_out" && done.Bool("finished") == true, "hard lifetime did not stop the worker");
        }
        using (var jobs = new ExecutionJobs(() => Worker("$p = Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30")))
        {
            var started = await jobs.StartAsync(new JsonObject { ["path"] = "unused-test-command", ["waitMs"] = 2000 });
            Require(int.TryParse(started.Str("stdout")?.Trim(), out int descendant), "descendant did not start");
            try
            {
                await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId"), ["action"] = "cancel", ["after"] = "999999" });
                throw new InvalidOperationException("invalid cancellation cursor accepted");
            }
            catch (ArgumentException) { }
            Require((await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId") })).Str("state") == "running", "invalid request cancelled a live job");
            var stopped = await jobs.ReadAsync(new JsonObject { ["jobId"] = started.Str("jobId"), ["action"] = "cancel", ["waitMs"] = 5000 });
            Require(stopped.Str("state") == "cancelled" && stopped.Bool("finished") == true, "job cancellation did not finish");
            try { using var child = Process.GetProcessById(descendant); Require(child.HasExited || child.WaitForExit(2000), "descendant survived job cancellation"); } catch (ArgumentException) { }
        }
        return "output/exit codes, job recovery, hard timeouts and cancellation of owned descendants";
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
