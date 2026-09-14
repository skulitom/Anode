using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Util;

namespace Anode.Core.Processes;

internal sealed class ExecutionJobs : IDisposable
{
    private sealed class Job : IDisposable
    {
        public readonly string Id = "j_" + Guid.NewGuid().ToString("N");
        public readonly DateTime Started = DateTime.UtcNow;
        public readonly ExecutionOutput Output = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Gate = new();
        public ExecutionJobObject? Owner;
        public Process? Worker;
        public string State = "running";
        public string? Error;
        public int? ExitCode;
        public int WorkerPid;
        public int Session;
        public DateTime? Finished;
        public void Stop(string reason)
        {
            lock (Gate)
            {
                if (State != "running") return;
                State = reason;
                Cancellation.Cancel();
            }
        }
        public void Dispose() { Owner?.Dispose(); Worker?.Dispose(); }
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly Func<ProcessStartInfo> _workerInfo;
    private bool _disposed;
    public ExecutionJobs(Func<ProcessStartInfo>? workerInfo = null) => _workerInfo = workerInfo ?? (() => new ProcessStartInfo(Env.ExecutablePath, "__exec-worker"));

    public async Task<JsonObject> StartAsync(JsonObject request, CancellationToken cancel = default)
    {
        if (Mcp.Tools.ValidateArguments("seat_exec", request) is { } error) throw new ArgumentException(error);
        if (Core.Steam.Steam.DirectLaunchFailure(request.Str("path")!, Session.ChildSession.CurrentSessionId()) is { } blocked)
            throw new InvalidOperationException(blocked);
        var job = new Job();
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExecutionJobs));
            if (_jobs.Values.Count(j => !j.Done.Task.IsCompleted) >= 8) throw new InvalidOperationException("Eight commands are already running. Finish or cancel one first.");
            while (_jobs.Count >= 32)
            {
                var old = _jobs.Values.Where(j => j.Done.Task.IsCompleted).MinBy(j => j.Started)!;
                _jobs.Remove(old.Id);
                old.Dispose();
            }
            _jobs.Add(job.Id, job);
        }
        // Supervision outlives this client call. Client cancellation never replays or
        // implicitly cancels an already started command; use the returned job ID.
        _ = SuperviseAsync(job, (JsonObject)request.DeepClone());
        return await ReadAsync(new JsonObject { ["jobId"] = job.Id, ["waitMs"] = request.Int("waitMs") ?? 1000 }, cancel);
    }

    private async Task SuperviseAsync(Job job, JsonObject request)
    {
        Task drains = Task.CompletedTask;
        using var timeout = new System.Threading.Timer(_ => job.Stop("timed_out"), null, request.Int("executionTimeoutMs") ?? 120000, Timeout.Infinite);
        try
        {
            var info = _workerInfo();
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            if (request.Str("cwd") is { } cwd) info.WorkingDirectory = cwd;
            job.Owner = new ExecutionJobObject();
            job.Worker = Process.Start(info) ?? throw new InvalidOperationException("Execution worker could not start.");
            job.WorkerPid = job.Worker.Id;
            job.Session = job.Worker.SessionId;
            job.Owner.Assign(job.Worker);
            Task stdout = DrainAsync(job.Worker.StandardOutput, "stdout", job.Output);
            Task stderr = DrainAsync(job.Worker.StandardError, "stderr", job.Output);
            drains = Task.WhenAll(stdout, stderr);
            await job.Worker.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), job.Cancellation.Token).ConfigureAwait(false);
            job.Worker.StandardInput.Close();
            await job.Worker.WaitForExitAsync(job.Cancellation.Token).ConfigureAwait(false);
            job.Owner.Terminate(); // Also closes pipes inherited by surviving descendants.
            await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            lock (job.Gate)
            {
                job.ExitCode = job.Worker.ExitCode;
                if (job.State == "running") job.State = "completed";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (job.Gate) { if (job.State == "running") job.State = "failed"; job.Error = ex.Message; }
        }
        finally
        {
            job.Owner?.Dispose();
            // Assignment can fail before the worker receives its request. It has not
            // started an application then, but still needs explicit cleanup.
            try { if (job.Worker is { HasExited: false }) job.Worker.Kill(); } catch { }
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { _ = drains.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            lock (job.Gate) job.Finished = DateTime.UtcNow;
            job.Done.TrySetResult();
        }
    }

    private static async Task DrainAsync(StreamReader reader, string channel, ExecutionOutput output)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) output.Append(channel, new string(buffer, 0, count));
    }

    public async Task<JsonObject> ReadAsync(JsonObject request, CancellationToken cancel = default)
    {
        if (Mcp.Tools.ValidateArguments("seat_job", request) is { } error) throw new ArgumentException(error);
        if (request.Str("action") == "list")
        {
            var items = new JsonArray();
            lock (_gate)
                foreach (var item in _jobs.Values.OrderBy(j => j.Started))
                    lock (item.Gate) items.Add(new JsonObject { ["jobId"] = item.Id, ["state"] = item.State,
                        ["finished"] = item.Done.Task.IsCompleted, ["exitCode"] = item.ExitCode, ["startedUtc"] = item.Started.ToString("O") });
            return new JsonObject { ["jobs"] = items, ["summary"] = items.Count == 0 ? "No execution jobs in this seat host."
                : string.Join("\n", items.OfType<JsonObject>().Select(j => $"{j.Str("jobId")}: {j.Str("state")}, started {j.Str("startedUtc")}")) };
        }
        Job job;
        lock (_gate)
            if (!_jobs.TryGetValue(request.Str("jobId")!, out job!)) throw new InvalidOperationException("Unknown jobId. Execution jobs belong to the current seat host and only the latest 32 are retained.");
        job.Output.Read(request.Str("after"), 0); // Reject a future cursor before cancellation or waiting.
        if (request.Str("action") == "cancel") job.Stop("cancelled");
        int wait = request.Int("waitMs") ?? 0;
        if (!job.Done.Task.IsCompleted && wait > 0)
            await Task.WhenAny(job.Done.Task, Task.Delay(wait, cancel)).WaitAsync(cancel).ConfigureAwait(false);
        JsonObject result = job.Output.Read(request.Str("after"), request.Int("maxChars") ?? 12000);
        lock (job.Gate)
        {
            result["jobId"] = job.Id;
            result["state"] = job.State;
            result["finished"] = job.Done.Task.IsCompleted;
            result["workerPid"] = job.WorkerPid;
            result["session"] = job.Session;
            result["exitCode"] = job.ExitCode;
            result["error"] = job.Error;
            result["elapsedMs"] = (long)((job.Finished ?? DateTime.UtcNow) - job.Started).TotalMilliseconds;
        }
        static string Printable(string text) => string.Concat(text.Select(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t' ? ' ' : c));
        result["summary"] = $"{job.Id}: {result.Str("state")}, exit code {result["exitCode"]?.ToString() ?? "pending"}. Cursor {result.Str("cursor")}."
            + (result.Bool("outputTruncated") == true ? " Earlier output was discarded; save complete logs from the command when needed." : "")
            + (result.Str("stdout") is { Length: > 0 } output ? "\nstdout:\n" + Printable(output) : "")
            + (result.Str("stderr") is { Length: > 0 } errors ? "\nstderr:\n" + Printable(errors) : "")
            + (result.Str("error") is { Length: > 0 } failure ? "\n" + Printable(failure) : "");
        return result;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var job in _jobs.Values) { job.Stop("cancelled"); job.Owner?.Dispose(); }
        }
    }
}
