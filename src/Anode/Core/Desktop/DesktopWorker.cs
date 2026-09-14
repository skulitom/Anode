using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Core.Desktop;

internal static class DesktopWorker
{
    public static int Run()
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            Seat.SeatHost.VerifyCurrentSession();
            using var input = new StreamReader(Console.OpenStandardInput());
            string line = input.ReadLine() ?? throw new ArgumentException("Missing worker request.");
            if (line.Length > 131072) throw new ArgumentException("Worker request is too large.");
            var request = JsonLine.Parse(line) ?? throw new ArgumentException("Invalid worker request.");
            // UI Automation must not run on the WinForms STA entry thread.
            var result = Task.Run(() => Execute(request)).GetAwaiter().GetResult();
            output.WriteLine(JsonLine.Ok(result).ToJsonString());
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(JsonLine.Fail(ex.Message).ToJsonString());
            return 1;
        }
    }

    internal static JsonObject Execute(JsonObject request)
    {
        string op = request.Str("op") ?? "";
        if (op == "windows") return new JsonObject { ["windows"] = WindowAccess.List() };
        if (op == "capabilities") return SeatCapabilities.Read(request.Bool("probeCapture") ?? true);
        var target = WindowTarget.FromJson(request.Obj("target") ?? throw new ArgumentException("Missing worker target."));
        return op switch
        {
            "observe" => AccessibilityReader.Observe(target, request),
            "element" => AccessibilityReader.Act(target, request.Obj("element") ?? throw new ArgumentException("Missing element."), request),
            "window" => WindowAccess.Act(target, request.Str("action") ?? "", request),
            _ => throw new ArgumentException("Unknown desktop worker operation.")
        };
    }

    public static async Task<JsonObject> CallAsync(JsonObject request, CancellationToken cancel = default)
    {
        var info = new ProcessStartInfo(Env.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("__desktop-worker");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the seat inspection worker.");
        return await ExchangeAsync(process, request, TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
    }

    internal static async Task<JsonObject> ExchangeAsync(Process process, JsonObject request, TimeSpan timeout, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        try
        {
            // Never log requests: value actions can contain document or form contents.
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string result = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (result.Length > 4_000_000) throw new InvalidOperationException("The accessibility result exceeded its size limit.");
            return ParseResult(result);
        }
        catch (OperationCanceledException)
        {
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException(request.Str("op") is "element" or "window"
                ? "The app did not finish the action within 10 seconds. It may already have taken effect. Inspect again; do not replay it."
                : "The app's accessibility provider did not respond within 10 seconds. The seat remains available; inspect another window or try a smaller tree.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    internal static JsonObject ParseResult(string json)
    {
        var envelope = JsonLine.Parse(json) ?? throw new InvalidOperationException("The seat worker returned no valid result.");
        if (envelope.Bool("ok") != true) throw new InvalidOperationException(envelope.Str("error") ?? "Seat inspection failed.");
        return envelope.Obj("result")?.DeepClone().AsObject() ?? new JsonObject();
    }
}
