using Anode.Core.Util;

namespace Anode.Core.Launch;

/// <summary>
/// Windows gives each session one child session, so Anodes on different channels take turns with
/// the seat instead of taking it from each other. Every daemon holds the seat mutex while it runs.
/// Anode 0.8.0 and earlier do not know that mutex, but they are always the main channel, and
/// their daemon mutex and pipes give them away.
/// </summary>
internal static class SeatSlot
{
    private const string SeatMutex = @"Local\Anode.Seat";
    private const string PipeRoot = @"\\.\pipe\";

    /// <summary>Why this channel cannot start a daemon now, or null. Launchers ask before starting one.</summary>
    public static string? Blocker()
    {
        // This channel's own daemon may be starting, and then it holds the seat itself.
        if (Exists(Env.DaemonMutex)) return null;
        var other = OtherChannel();
        return other is not null || Exists(SeatMutex) ? Taken(other) : null;
    }

    /// <summary>
    /// Takes the seat for this channel's daemon until disposed, or returns null and says who has it.
    /// The caller holds this channel's daemon mutex and disposes the claim on the same thread.
    /// </summary>
    public static IDisposable? Claim(out string? blocker) => Claim(SeatMutex, OtherChannel, out blocker);

    internal static IDisposable? Claim(string seatMutex, Func<(string Channel, bool Running)?> otherChannel, out string? blocker)
    {
        var mutex = new Mutex(false, seatMutex);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // left by a daemon that crashed
        var other = otherChannel();
        blocker = owned && other is null ? null : Taken(other);
        if (blocker is null) return new Claimed(mutex);
        if (owned) mutex.ReleaseMutex();
        mutex.Dispose();
        return null;
    }

    /// <summary>Another channel's Anode in this Windows session: its daemon, or a seat it kept when it quit.</summary>
    internal static (string Channel, bool Running)? OtherChannel() =>
        OtherChannel(Env.Channel, Pipes(), !Env.IsMainChannel && Exists(Env.MainDaemonMutex));

    internal static (string Channel, bool Running)? OtherChannel(string own, IEnumerable<string> pipes, bool mainDaemon)
    {
        (string, bool)? kept = null;
        foreach (string pipe in pipes)
        {
            if (ChannelOf(pipe, "anode-control") is { } running && running != own) return (running, true);
            if (ChannelOf(pipe, "anode-seat") is { } seat && seat != own) kept ??= (seat, false);
        }
        // Anode 0.8.0 and earlier take only the main daemon mutex, and before their pipe exists.
        return own != Env.MainChannel && mainDaemon ? (Env.MainChannel, true) : kept;
    }

    internal static string Taken((string Channel, bool Running)? other)
    {
        string mine = Env.IsMainChannel ? "this Anode" : $"the {Env.Channel} Anode";
        string rule = $"Windows allows one seat per session, so {mine} will not start a second one or take that one over.";
        if (other is not { } found) return $"Another Anode has this Windows session's seat. {rule} Try again once it quits.";
        return found.Running
            ? $"The {found.Channel} Anode is running this Windows session's seat. {rule} Try again once it quits; "
                + "`anode quit` closes every program in its seat."
            : $"The {found.Channel} Anode kept its seat signed in when it quit. {rule} Start and quit that Anode to sign "
                + "the seat out, then try again.";
    }

    private static string? ChannelOf(string pipe, string name)
    {
        if (pipe.Equals(name, StringComparison.OrdinalIgnoreCase)) return Env.MainChannel;
        if (!pipe.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)) return null;
        string channel = pipe[(name.Length + 1)..].ToLowerInvariant();
        return Env.IsChannelName(channel) ? channel : null;
    }

    /// <summary>Named pipes are machine-wide, so a seat host inside the child session is listed too.</summary>
    internal static IReadOnlyList<string> Pipes()
    {
        try
        {
            return Directory.GetFiles(PipeRoot)
                .Select(path => path.StartsWith(PipeRoot, StringComparison.OrdinalIgnoreCase) ? path[PipeRoot.Length..] : path)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("could not list named pipes to find other Anode channels: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static bool Exists(string mutex)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutex, out var handle)) return false;
            handle.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class Claimed : IDisposable
    {
        private readonly Mutex _mutex;

        public Claimed(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // its thread ended first; Windows frees an abandoned mutex
            _mutex.Dispose();
        }
    }
}

/// <summary>Another channel's Anode has this Windows session's one seat.</summary>
internal sealed class SeatTakenException : InvalidOperationException
{
    public SeatTakenException(string message) : base(message) { }
}
