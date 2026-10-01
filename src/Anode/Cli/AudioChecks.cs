using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Audio;
using Anode.Core.Bridge;
using Anode.Mcp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Anode.Cli;

/// <summary>No endpoints, microphone, speakers, seat, or production pipes are accessed.</summary>
internal static class AudioChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static string Clips()
    {
        var pcm = new byte[AudioClip.SampleRate * AudioClip.BlockAlign];
        byte[] packet = { 0, 128, 255, 127, 0, 64, 0, 64 };
        AudioClip.Place(pcm, packet, 24000);
        Require(pcm.Take(24000 * 4).All(b => b == 0) && pcm.AsSpan(24000 * 4, packet.Length).SequenceEqual(packet), "leading silence collapsed");
        Require(pcm.Skip(24000 * 4 + packet.Length).All(b => b == 0), "trailing silence lost");
        Require(AudioClip.Peak(pcm) == 1, "negative full-scale sample overflowed");
        byte[] wave = AudioClip.Wave(pcm);
        Require(AudioClip.ValidateWave(wave) == TimeSpan.FromSeconds(1), "WAV duration/header incorrect");
        using (var reader = new WaveFileReader(new MemoryStream(wave)))
            Require(reader.WaveFormat.SampleRate == 48000 && reader.WaveFormat.Channels == 2 && reader.Length == pcm.Length, "WAV format incorrect");
        var clipped = new byte[8];
        AudioClip.Place(clipped, packet, -1);
        Require(clipped.AsSpan(0, 4).SequenceEqual(packet.AsSpan(4, 4)), "negative timestamp not trimmed");
        AudioClip.Place(clipped, packet, 1);
        Require(clipped.AsSpan(4, 4).SequenceEqual(packet.AsSpan(0, 4)), "end timestamp not trimmed");
        AudioClip.Place(clipped, packet, 100000);
        Require(AudioClip.Peak(new byte[AudioClip.SampleRate * 4]) == 0, "silence incorrectly reported as sound");
        byte[] corrupt = (byte[])wave.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(16, 4), uint.MaxValue);
        foreach (byte[] bad in new[] { new byte[44], corrupt, wave[..^1] })
        {
            bool refused = false;
            try { AudioClip.ValidateWave(bad); } catch (ArgumentException) { refused = true; }
            Require(refused, "malformed/oversized chunk accepted");
        }
        return "PCM WAV roundtrip, silent gaps, clipped timestamps, full-scale samples and malformed chunks";
    }

    internal static string IsolationAndValidation()
    {
        Require(SeatAudio.IsRemoteRender(DataFlow.Render, DeviceState.Active, 0), "Remote Audio rejected");
        foreach (uint? factor in new uint?[] { null, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
            Require(!SeatAudio.IsRemoteRender(DataFlow.Render, DeviceState.Active, factor), "physical or unknown output accepted");
        Require(!SeatAudio.IsRemoteRender(DataFlow.Capture, DeviceState.Active, 0)
            && !SeatAudio.IsRemoteRender(DataFlow.Render, DeviceState.Disabled, 0), "microphone or disabled endpoint accepted");
        Require(!AgentAccess.RequiresLease("audio.status") && new[] { "audio.listen", "audio.play", "audio.stop" }.All(AgentAccess.RequiresLease), "audio lease requirements incorrect");
        foreach (var invalid in new[] { new JsonObject(), new JsonObject { ["path"] = "relative.wav" },
            new JsonObject { ["path"] = @"\\server\clip.wav" }, new JsonObject { ["data"] = "invalid" },
            new JsonObject { ["path"] = @"C:\clip.wav", ["data"] = "invalid" }, new JsonObject { ["path"] = @"C:\clip.mp3" } })
            Require(Tools.ValidateArguments("seat_audio_play", invalid) is not null, "invalid playback arguments accepted");
        Require(Tools.ValidateArguments("seat_audio_play", new JsonObject { ["path"] = @"C:\clip.wav" }) is null, "local WAV path rejected");
        Require(Tools.ValidateArguments("seat_audio_listen", new JsonObject { ["durationMs"] = 30001 }) is not null
            && Tools.ValidateArguments("seat_audio_listen", new JsonObject { ["durationMs"] = 99 }) is not null, "unbounded recording accepted");
        return "only active remote render endpoints, lease gating, closed schemas and bounded inputs";
    }

    internal static async Task<string> Protocol()
    {
        string pipe = "anode-selftest-audio-" + Guid.NewGuid().ToString("N");
        string base64 = Convert.ToBase64String(AudioClip.Wave(new byte[480 * 4]));
        int calls = 0;
        using var daemon = new JsonPipeServer(pipe, request =>
        {
            calls++;
            if (request.Str("op") == "lease") return Task.FromResult(JsonLine.Ok(new JsonObject { ["leaseToken"] = "audio-test" }));
            Require(request.Str("op") == "audio.listen" && request.Str("leaseToken") == "audio-test", "audio lost its lease or operation mapping");
            return Task.FromResult(JsonLine.Ok(new JsonObject { ["data"] = base64, ["mimeType"] = "audio/wav", ["durationMs"] = 100,
                ["silent"] = true, ["summary"] = "Silent seat audio" }));
        });
        daemon.Start();
        using var backend = McpChecks.Isolated(pipe);
        var invalid = await backend.CallAsync("seat_audio_play", new JsonObject { ["data"] = "bad" }, CancellationToken.None);
        Require(invalid.Bool("isError") == true && calls == 0, "invalid audio reached a daemon or acquired a lease");
        var response = await backend.CallAsync("seat_audio_listen", new JsonObject { ["durationMs"] = 100 }, CancellationToken.None);
        var content = (JsonArray)response["content"]!;
        var audio = content.OfType<JsonObject>().Single(c => c.Str("type") == "audio");
        Require(audio.Str("mimeType") == "audio/wav" && audio.Str("data") == base64, "MCP did not return native audio");
        Require(response["structuredContent"] is null && !McpChecks.Text(response).Contains(base64)
            && McpChecks.Text(response).Contains("durationMs"), "audio hidden by structured data or duplicated as text");
        Require(calls == 2, "audio did not acquire exactly one lease before recording");
        // MCP 2024-11-05 predates audio content, so its clients receive the WAV as an embedded resource.
        backend.Initialized("selftest", "2024-11-05");
        var legacy = await backend.CallAsync("seat_audio_listen", new JsonObject { ["durationMs"] = 100 }, CancellationToken.None);
        var blocks = ((JsonArray)legacy["content"]!).OfType<JsonObject>().ToArray();
        var resource = blocks.FirstOrDefault()?.Obj("resource");
        Require(blocks.Length == 2 && blocks[0].Str("type") == "resource" && resource?.Str("blob") == base64
            && resource.Str("mimeType") == "audio/wav" && resource.Str("uri")?.StartsWith("anode://seat/audio/", StringComparison.Ordinal) == true
            && blocks[1].Str("type") == "text" && McpChecks.Text(legacy).Contains("durationMs") && legacy["structuredContent"] is null,
            "a 2024-11-05 client received audio content, which its protocol does not define");
        backend.Initialized("selftest", "2025-03-26");
        var boundary = await backend.CallAsync("seat_audio_listen", new JsonObject { ["durationMs"] = 100 }, CancellationToken.None);
        Require(((JsonArray)boundary["content"]!).OfType<JsonObject>().Any(c => c.Str("type") == "audio" && c.Str("data") == base64),
            "a 2025-03-26 client did not receive native audio");
        Require(calls == 4, "the held lease was not reused for later recordings");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await SeatAudio.ListenAsync(100, cancelled.Token); throw new InvalidOperationException("cancelled capture continued"); }
        catch (OperationCanceledException) { }
        return "private-pipe validation before dispatch, lease acquisition, native MCP audio, an embedded WAV for 2024-11-05 clients and pre-cancelled capture";
    }

    internal static async Task<string> Lifecycle()
    {
        int active = 0, starts = 0;
        using var audio = new SeatAudio((_, ready, cancel) =>
        {
            Interlocked.Increment(ref active); Interlocked.Increment(ref starts);
            try
            {
                ready(new JsonObject { ["state"] = "playing" });
                cancel.WaitHandle.WaitOne();
                cancel.ThrowIfCancellationRequested();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        long now = 0;
        var lease = new DesktopLease(() => audio.StopPlayback(), milliseconds: () => now);
        Task<JsonObject> Dispatch(JsonObject request, CancellationToken cancel) => request.Str("op") == "audio.play"
            ? audio.PlayAsync(AgentAccess.Arguments(request), cancel) : Task.FromResult(audio.StopPlayback());
        async Task<string> Acquire() => (await lease.HandleAsync(new JsonObject { ["op"] = "lease", ["action"] = "acquire", ["agentId"] = "audio-agent" }, Dispatch)).Obj("result")!.Str("leaseToken")!;
        var source = new JsonObject { ["data"] = Convert.ToBase64String(AudioClip.Wave(new byte[480 * 4])) };
        string token = await Acquire();
        var play = AgentAccess.Attach(source, "audio-agent", token); play["op"] = "audio.play";
        await lease.HandleAsync(play, Dispatch);
        Require(active == 1, "playback was not left running after start");
        bool refused = false;
        try { await audio.PlayAsync(source, CancellationToken.None); } catch (InvalidOperationException) { refused = true; }
        Require(refused && starts == 1, "a second play replaced uncertain playback");
        var intruder = await lease.HandleAsync(new JsonObject { ["op"] = "audio.stop", ["agentId"] = "another-agent", ["leaseToken"] = token }, Dispatch);
        Require(intruder.Bool("ok") == false && active == 1, "another agent stopped playback");
        await lease.HandleAsync(new JsonObject { ["op"] = "lease", ["action"] = "release", ["agentId"] = "audio-agent", ["leaseToken"] = token }, Dispatch);
        Require(active == 0, "lease release left playback alive");
        token = await Acquire(); play["leaseToken"] = token;
        await lease.HandleAsync(play, Dispatch);
        now = 120001; lease.ExpireIdle();
        Require(active == 0, "lease expiry left playback alive");
        using var failure = new SeatAudio((_, _, _) => throw new InvalidOperationException("injected endpoint failure"));
        try { await failure.PlayAsync(source, CancellationToken.None); throw new Exception("startup failure was swallowed"); }
        catch (InvalidOperationException ex) when (ex.Message == "injected endpoint failure") { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await audio.PlayAsync(source, cancelled.Token); throw new Exception("cancelled playback started"); }
        catch (OperationCanceledException) { }
        Require(starts == 2, "cancelled startup dispatched playback");
        return "single playback, owner-only stop, release/expiry cleanup, startup failure and cancellation using an injected renderer";
    }
}
