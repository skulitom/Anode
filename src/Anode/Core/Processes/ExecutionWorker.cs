using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Session;

namespace Anode.Core.Processes;

internal static class ExecutionWorker
{
    public static int Run()
    {
        try
        {
            Seat.SeatHost.VerifyCurrentSession();
            // The host assigns this worker to its Windows job before sending a request.
            // No application can start before that ownership boundary exists.
            string line = Console.In.ReadLine() ?? throw new ArgumentException("Missing execution request.");
            if (line.Length > 262144) throw new ArgumentException("Execution request is too large.");
            var request = JsonLine.Parse(line) ?? throw new ArgumentException("Invalid execution request.");
            if (Mcp.Tools.ValidateArguments("seat_exec", request) is { } error) throw new ArgumentException(error);
            string path = request.Str("path")!;
            if (Core.Steam.Steam.DirectLaunchFailure(path, ChildSession.CurrentSessionId()) is { } blocked)
                throw new InvalidOperationException(blocked);
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = request.Str("cwd") ?? Environment.CurrentDirectory
            };
            foreach (var argument in request["args"] as JsonArray ?? new()) info.ArgumentList.Add(argument!.GetValue<string>());
            if (request.Obj("env") is { } variables)
                foreach (var (key, value) in variables) info.Environment[key] = value!.GetValue<string>();
            using var child = Process.Start(info) ?? throw new InvalidOperationException("The executable did not start.");
            child.StandardInput.Close(); // Noninteractive commands only; no hidden password wait.
            Task stdout = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            Task stderr = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            child.WaitForExit();
            // Descendants holding inherited pipe handles must not stall completion forever.
            Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(2));
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Anode execution: " + ex.Message);
            return 1;
        }
    }
}
