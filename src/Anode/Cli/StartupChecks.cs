using System.Diagnostics;
using System.IO.Pipes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Daemon;

namespace Anode.Cli;

/// <summary>Startup orchestration with private pipes, private mutexes and injected host launches.</summary>
internal static class StartupChecks
{
    private static string Name() => "anode-selftest-" + Guid.NewGuid().ToString("N");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task<string> DaemonDiscovery()
    {
        string pipe = Name(), mutexName = @"Local\" + Name();
        var clock = Stopwatch.StartNew();
        using var absent = await DaemonLauncher.ConnectExistingAsync(pipe, mutexName, 5000, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Require(absent is null, "an absent daemon was reported connected");
        long absentMs = clock.ElapsedMilliseconds;

        // The daemon takes its mutex before creating the pipe. Do not mistake that startup gap for absence.
        using var mutex = new Mutex(false, mutexName);
        using var server = new JsonPipeServer(pipe, _ => Task.FromResult(JsonLine.Ok()));
        var connecting = DaemonLauncher.ConnectExistingAsync(pipe, mutexName, 3000, default);
        Require(!connecting.IsCompleted, "discovery did not wait for a daemon that already has its mutex");
        server.Start();
        using var connected = await connecting.WaitAsync(TimeSpan.FromSeconds(4));
        Require(connected is not null && (await connected.RequestAsync("ping")).Bool("ok") == true,
            "the delayed daemon did not connect");

        // A listening older host remains usable even if it does not publish the expected mutex.
        using var compatible = await DaemonLauncher.ConnectExistingAsync(pipe, @"Local\" + Name(), 3000, default);
        Require(compatible is not null, "a listening host without a mutex was ignored");

        using var cancel = new CancellationTokenSource();
        var cancelled = DaemonLauncher.ConnectExistingAsync(Name(), mutexName, 5000, cancel.Token);
        cancel.Cancel();
        try { using var unexpected = await cancelled; throw new InvalidOperationException("daemon discovery ignored cancellation"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        return $"absent daemon returns in {absentMs} ms; starting, compatible and cancelled daemons handled without launching anything";
    }

    public static async Task<string> HostConnection()
    {
        string freshPipe = Name();
        using var freshServer = new JsonPipeServer(freshPipe, _ => Task.FromResult(JsonLine.Ok()));
        int launches = 0;
        var fresh = AnodeDaemon.ConnectHostAsync(freshPipe, () => { launches++; freshServer.Start(); }, default);
        using var connected = await fresh.WaitAsync(TimeSpan.FromSeconds(3));
        Require(connected is not null && launches == 1, "new host did not launch and connect exactly once");

        string retainedPipe = Name();
        using var retainedServer = new JsonPipeServer(retainedPipe, _ => Task.FromResult(JsonLine.Ok()));
        var retained = AnodeDaemon.ConnectHostAsync(retainedPipe,
            () => throw new InvalidOperationException("a retained host was launched twice"), default);
        retainedServer.Start();
        using var reused = await retained.WaitAsync(TimeSpan.FromSeconds(3));
        Require(reused is not null && (await reused.RequestAsync("ping")).Bool("ok") == true,
            "reconnect did not wait for the retained host");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            using var unexpected = await AnodeDaemon.ConnectHostAsync(Name(),
                () => throw new InvalidOperationException("cancelled startup launched a host"), cancel.Token);
            throw new InvalidOperationException("host launch ignored cancellation");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        return "missing hosts launch once; retained hosts are reused; cancellation prevents a launch";
    }

    public static async Task<string> StateNotifications()
    {
        using var daemon = new AnodeDaemon(new SeatOptions(), () => null, () => null,
            logoff: _ => null, disconnectViewer: () => { });
        var first = daemon.StartSeatAsync();
        var second = daemon.StartSeatAsync();
        var clock = Stopwatch.StartNew();
        daemon.OnViewerLogonError(0);
        var replies = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Require(replies.All(reply => reply.Bool("ok") == false && reply.Str("error")!.Contains("sign in")),
            "concurrent startup callers missed the terminal error");
        long failureMs = clock.ElapsedMilliseconds;

        var stopped = daemon.StartSeatAsync();
        await daemon.StopSeatAsync("private startup check; no live seat");
        // Restart before the old waiter's continuation necessarily executes.
        var restarted = daemon.StartSeatAsync();
        Require((await stopped.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false,
            "a stopped startup followed a later attempt");
        daemon.OnViewerDisconnected(1800);
        Require((await restarted.WaitAsync(TimeSpan.FromSeconds(2))).Str("error")!.Contains("1800"),
            "a restarted caller missed the new failure");
        return $"all startup waiters wake on state changes ({failureMs} ms for failure), and Stop cannot be undone by a later start";
    }

    /// <summary>
    /// B-051: a program Anode starts to outlive the command, such as a fallback daemon or the sign-in browser, must not
    /// hold the caller's output pipe. A stand-in (ping, about 3 s) starts while this process holds an inheritable pipe
    /// end, as a CLI whose output is piped does; once this process closes its end, the reader must see the end at once.
    /// The control starts the same stand-in the old way, with Process.Start, and its reader must still be waiting.
    /// Each stand-in is held by its process handle, so ending it can't reach a recycled process id. No daemon starts.
    /// </summary>
    public static string LaunchHandles()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        string[] arguments = { "-n", "4", "127.0.0.1" };

        static bool Ends(Func<Process> start, out Process started)
        {
            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            started = start();
            pipe.DisposeLocalCopyOfClientHandle();
            var read = pipe.ReadAsync(new byte[1]).AsTask();
            return read.Wait(1500) && read.Result == 0;
        }

        Process? detached = null, control = null;
        try
        {
            bool free = Ends(() =>
            {
                var process = Process.GetProcessById(DetachedProcess.Start(ping, arguments, null));
                _ = process.Handle;
                return process;
            }, out detached);
            Require(!detached.HasExited, "the stand-in was not running, so the check proves nothing");
            Require(free, "a program started detached kept the caller's pipe open");

            bool held = !Ends(() =>
            {
                var info = new ProcessStartInfo(ping) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                return Process.Start(info) ?? throw new InvalidOperationException("could not start ping");
            }, out control);
            Require(held, "the control's Process.Start child did not hold the pipe, so the check proves nothing");
            return "a detached start passes none of the caller's handles on; the control (Process.Start) holds the pipe";
        }
        finally
        {
            foreach (var process in new[] { detached, control })
            {
                if (process is null) continue;
                // Kill returns quietly once the stand-in has exited; nothing here may hide the check's own failure.
                try { process.Kill(); process.WaitForExit(5000); }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }
}
