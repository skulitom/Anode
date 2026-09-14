using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace Anode.Core.Session;

/// <summary>Some RDP failures reach the Windows log before the ActiveX disconnect callback.</summary>
internal static class RdpStartupDiagnostics
{
    internal const string ClientLog = "Microsoft-Windows-TerminalServices-RDPClient/Operational";

    internal static string? FindFailure(DateTime startedUtc)
    {
        string since = startedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        string query = $"*[System[(EventID=226) and Execution[@ProcessID={Environment.ProcessId}] "
            + $"and TimeCreated[@SystemTime>='{since}']]]";
        using var reader = new EventLogReader(new EventLogQuery(ClientLog, PathType.LogName, query)
        { ReverseDirection = true });
        for (int count = 0; count < 8; count++)
        {
            using var record = reader.ReadEvent();
            if (record is null) return null;
            if (record.ProviderName != "Microsoft-Windows-TerminalServices-ClientActiveXCore") continue;
            if (DescribeFailure(record.ProcessId, record.TimeCreated, startedUtc,
                record.Properties.Select(p => p.Value).ToArray()) is { } failure) return failure;
        }
        return null;
    }

    internal static string? DescribeFailure(int? processId, DateTime? time, DateTime startedUtc, object?[] values)
    {
        // Ignore other clients, previous attempts and recoverable transport warnings.
        if (processId != Environment.ProcessId || time is null || time.Value.ToUniversalTime() < startedUtc.ToUniversalTime()
            || values.Length < 8 || values[0] as string != "RDPClient_SSL"
            || values[4] as string != "TsSslStateDisconnecting"
            || values[6] as string != "TsSslEventStartHandshakeFailed") return null;
        if (!uint.TryParse(Convert.ToString(values[7], CultureInfo.InvariantCulture), out uint code)) return null;
        return $"Windows failed the child-session authentication handshake (0x{code:X8}; RDP client event 226). "
            + $"See {ClientLog}. The listener can be running while authentication fails.";
    }
}
