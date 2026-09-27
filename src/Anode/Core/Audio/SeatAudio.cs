using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Util;
using Anode.Seat;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Anode.Core.Audio;

/// <summary>Audio is opened only in a verified child session, on its remote render endpoint.</summary>
internal sealed class SeatAudio : IDisposable
{
    private const string Unavailable = "No isolated Remote Audio output is available. At the next planned restart, run `anode start --hidden --audio`. "
        + "This enables sound from seat apps and sends it to the user's speakers. Do not restart an occupied seat. "
        + "If already enabled, check Windows Audio and Remote Desktop audio policies. Physical audio devices are never used.";
    private readonly object _gate = new();
    private CancellationTokenSource? _playCancel;
    private Task? _playTask;
    private string _playState = "idle";
    private string? _playError;
    private readonly Action<byte[], Action<JsonObject>, CancellationToken> _render;

    internal SeatAudio(Action<byte[], Action<JsonObject>, CancellationToken>? render = null) => _render = render ?? Render;

    internal static bool IsRemoteRender(DataFlow flow, DeviceState state, uint? formFactor) =>
        flow == DataFlow.Render && state == DeviceState.Active && formFactor == 0; // RemoteNetworkDevice (mmdeviceapi.h)

    private static MMDevice OpenEndpoint()
    {
        SeatHost.VerifyCurrentSession();
        using var devices = new MMDeviceEnumerator();
        // RDP supplies a session-specific endpoint. A physical endpoint's loopback can contain
        // OTHER sessions, even when opened from the seat. Never fall back to such an endpoint,
        // a friendly-name match, the microphone, or automatic default-device stream routing.
        MMDevice device;
        try { device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (Exception ex) { throw new InvalidOperationException(Unavailable, ex); }
        try
        {
            uint? factor = device.Properties.TryGetValue<uint>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out var value) ? value : null;
            if (!IsRemoteRender(device.DataFlow, device.State, factor)) throw new InvalidOperationException(Unavailable);
            return device;
        }
        catch { device.Dispose(); throw; }
    }

    internal static JsonObject Availability()
    {
        try
        {
            using var device = OpenEndpoint();
            return new JsonObject { ["available"] = true, ["device"] = device.FriendlyName, ["deviceId"] = device.ID,
                ["captureTested"] = false, ["speakerRedirection"] = true,
                ["summary"] = "Remote Audio endpoint available. Listening captures the seat's app mix; playback is also redirected to the user's speakers. Capture has not been tested." };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["available"] = false, ["captureTested"] = false, ["error"] = ex.Message, ["summary"] = ex.Message };
        }
    }

    internal JsonObject Status()
    {
        var result = Availability();
        lock (_gate)
        {
            result["playbackState"] = _playState;
            result["playbackError"] = _playError;
            result["summary"] = result.Str("summary") + $" Agent playback: {_playState}." + (_playError is null ? "" : " " + _playError);
        }
        return result;
    }

    internal static Task<JsonObject> ListenAsync(int durationMs, CancellationToken cancel) => Task.Run(() =>
    {
        if (durationMs is < 100 or > AudioClip.MaxListenMs) throw new ArgumentOutOfRangeException(nameof(durationMs));
        cancel.ThrowIfCancellationRequested();
        using var device = OpenEndpoint();
        using var client = device.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback
            | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            1_000_000, 0, AudioClip.Format, Guid.Empty);
        var capture = client.AudioCaptureClient;
        var pcm = new byte[durationMs * AudioClip.SampleRate / 1000 * AudioClip.BlockAlign];
        long origin = Qpc100ns();
        long fallbackFrame = 0;
        int packets = 0, discontinuities = 0, timestampErrors = 0;
        client.Start();
        var clock = Stopwatch.StartNew();
        try
        {
            // Drain the last engine period too; timestamps trim it to the requested interval.
            while (clock.ElapsedMilliseconds < durationMs + 100)
            {
                cancel.ThrowIfCancellationRequested();
                while (clock.ElapsedMilliseconds < durationMs + 100 && capture.GetNextPacketSize() > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    IntPtr buffer = capture.GetBuffer(out int frames, out var flags, out _, out long timestamp);
                    try
                    {
                        int bytes = checked(frames * AudioClip.BlockAlign);
                        if (bytes > AudioClip.MaxBytes) throw new InvalidOperationException("Audio packet exceeds its size limit.");
                        var packet = new byte[bytes];
                        if (!flags.HasFlag(AudioClientBufferFlags.Silent)) Marshal.Copy(buffer, packet, 0, bytes);
                        bool badTime = flags.HasFlag(AudioClientBufferFlags.TimestampError);
                        if (badTime) timestampErrors++;
                        if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) discontinuities++;
                        long frame = badTime ? Math.Max(fallbackFrame, (Qpc100ns() - origin) * AudioClip.SampleRate / 10_000_000 - frames)
                            : (timestamp - origin) * AudioClip.SampleRate / 10_000_000;
                        AudioClip.Place(pcm, packet, frame);
                        fallbackFrame = frame + frames;
                        packets++;
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
                if (cancel.WaitHandle.WaitOne(10)) cancel.ThrowIfCancellationRequested();
            }
        }
        finally { try { client.Stop(); } catch (Exception ex) { Log.Warn("audio capture stop: " + ex.Message); } }
        double peak = AudioClip.Peak(pcm);
        byte[] wave = AudioClip.Wave(pcm);
        return new JsonObject { ["mimeType"] = "audio/wav", ["data"] = Convert.ToBase64String(wave),
            ["bytes"] = wave.Length, ["durationMs"] = durationMs, ["sampleRate"] = AudioClip.SampleRate,
            ["channels"] = AudioClip.Channels, ["bitsPerSample"] = 16, ["peak"] = peak, ["silent"] = peak == 0,
            ["packets"] = packets, ["discontinuities"] = discontinuities, ["timestampErrors"] = timestampErrors,
            ["summary"] = $"Seat audio, {durationMs} ms, 48 kHz stereo WAV. " + (peak == 0 ? "No audible samples captured." : $"Peak {peak:P0}.")
                + (discontinuities + timestampErrors > 0 ? " Capture reported timing gaps; see metadata." : "") };
    }, cancel);

    private static long Qpc100ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));

    internal async Task<JsonObject> PlayAsync(JsonObject args, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        byte[] bytes = AudioClip.ReadSource(args);
        var started = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            if (_playTask is { IsCompleted: false }) throw new InvalidOperationException("Agent audio is already playing; stop it before starting another clip.");
            _playCancel?.Dispose();
            _playCancel = lifetime = new CancellationTokenSource(AudioClip.MaxPlayMs + 5000);
            _playState = "starting"; _playError = null;
            _playTask = Task.Run(() => Play(bytes, started, lifetime.Token));
        }
        // Cancelling startup must not leave an uncertain new playback running.
        using var registration = cancel.Register(() => lifetime.Cancel());
        return await started.Task.WaitAsync(cancel).ConfigureAwait(false);
    }

    private void Play(byte[] bytes, TaskCompletionSource<JsonObject> started, CancellationToken cancel)
    {
        try
        {
            cancel.ThrowIfCancellationRequested();
            _render(bytes, result =>
            {
                lock (_gate) _playState = "playing";
                started.TrySetResult(result);
            }, cancel);
            lock (_gate) _playState = "completed";
        }
        catch (OperationCanceledException)
        {
            started.TrySetCanceled(cancel);
            lock (_gate) _playState = "stopped";
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            lock (_gate) { _playState = "failed"; _playError = ex.Message; }
        }
    }

    private static void Render(byte[] bytes, Action<JsonObject> started, CancellationToken cancel)
    {
        using var device = OpenEndpoint();
        using var reader = new WaveFileReader(new MemoryStream(bytes, writable: false));
        using var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(100).Build();
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) => ended.TrySetResult(e.Exception);
        player.Init(reader);
        cancel.ThrowIfCancellationRequested();
        player.Play();
        started(new JsonObject { ["state"] = "playing", ["durationMs"] = (int)reader.TotalTime.TotalMilliseconds,
            ["summary"] = "Playing WAV in the seat. Use seat_audio_listen to hear it, seat_audio_status to check completion, or seat_audio_stop to stop. Releasing the desktop lease stops agent playback." });
        try { ended.Task.WaitAsync(cancel).GetAwaiter().GetResult(); }
        finally { player.Stop(); }
        if (ended.Task.Result is { } error) throw error;
    }

    internal void CancelPlayback() { lock (_gate) _playCancel?.Cancel(); }

    internal JsonObject StopPlayback()
    {
        Task? task;
        lock (_gate) { _playCancel?.Cancel(); task = _playTask; }
        if (task is not null && !task.Wait(3000)) throw new InvalidOperationException("Audio playback has not stopped yet; desktop transfer remains blocked.");
        return new JsonObject { ["state"] = "stopped", ["summary"] = "Agent audio stopped. Browser and game audio are controlled by their apps." };
    }

    public void Dispose()
    {
        StopPlayback();
        lock (_gate) { _playCancel?.Dispose(); _playCancel = null; }
    }
}
