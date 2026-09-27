using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using NAudio.Wave;

namespace Anode.Core.Audio;

/// <summary>Bounded PCM clips shared by the seat, CLI and device-free regression checks.</summary>
internal static class AudioClip
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal const int MaxListenMs = 30000;
    internal const int MaxPlayMs = 120000;
    internal const int SampleRate = 48000;
    internal const int Channels = 2;
    internal const int BlockAlign = Channels * sizeof(short);
    internal static WaveFormat Format => new(SampleRate, 16, Channels);

    internal static string? ValidateSource(JsonObject args)
    {
        if (args.ContainsKey("path") == args.ContainsKey("data")) return "Provide exactly one of path or data (base64 WAV).";
        if (args.Str("path") is { } path && (path.Length is 0 or > 1024 || path.Contains('\0')
            || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            return "path must be an absolute local .wav file path (not a network path).";
        if (args.Str("data") is { } data)
        {
            if (data.Length is 0 || data.Length > (MaxBytes + 2) / 3 * 4) return "Audio is limited to 8 MiB.";
            try { ValidateWave(Convert.FromBase64String(data)); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or EndOfStreamException)
            { return "Invalid WAV data: " + ex.Message; }
        }
        return null;
    }

    internal static byte[] ReadSource(JsonObject args)
    {
        if (ValidateSource(args) is { } invalid) throw new ArgumentException(invalid);
        byte[] bytes;
        if (args.Str("data") is { } data) bytes = Convert.FromBase64String(data);
        else
        {
            using var file = new FileStream(args.Str("path")!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 44 or > MaxBytes) throw new ArgumentException("WAV files must contain 44 bytes to 8 MiB.");
            bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
        }
        ValidateWave(bytes);
        return bytes;
    }

    internal static TimeSpan ValidateWave(byte[] bytes)
    {
        if (bytes.Length is < 44 or > MaxBytes) throw new ArgumentException("WAV files must contain 44 bytes to 8 MiB.");
        // Validate chunk boundaries before the decoder sees an untrusted chunk length.
        if (!bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8)
            throw new ArgumentException("Expected a complete RIFF/WAVE file.");
        bool formatSeen = false, dataSeen = false;
        for (long offset = 12; offset < bytes.Length;)
        {
            if (offset + 8 > bytes.Length) throw new ArgumentException("Truncated WAV chunk.");
            var chunk = bytes.AsSpan((int)offset, 8);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long next = offset + 8 + length + (length & 1);
            if (next > bytes.Length) throw new ArgumentException("WAV chunk exceeds the file.");
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (formatSeen || length is < 16 or > 40) throw new ArgumentException("Unsupported WAV format chunk.");
                formatSeen = true;
            }
            if (chunk[..4].SequenceEqual("data"u8))
            {
                if (dataSeen || !formatSeen) throw new ArgumentException("Expected one data chunk after the WAV format.");
                dataSeen = true;
            }
            offset = next;
        }
        if (!formatSeen || !dataSeen) throw new ArgumentException("WAV requires format and data chunks.");
        using var wave = new WaveFileReader(new MemoryStream(bytes, writable: false));
        var format = wave.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16
            || format.Channels is < 1 or > 2 || format.SampleRate is < 8000 or > 96000
            || format.BlockAlign != format.Channels * 2 || format.AverageBytesPerSecond != format.SampleRate * format.BlockAlign
            || wave.Length == 0 || wave.Length % format.BlockAlign != 0)
            throw new ArgumentException("Use 16-bit PCM WAV, mono or stereo, 8000-96000 Hz, with complete sample frames.");
        if (wave.TotalTime.TotalMilliseconds > MaxPlayMs) throw new ArgumentException("Playback is limited to 120 seconds.");
        return wave.TotalTime;
    }

    internal static byte[] Wave(byte[] pcm)
    {
        using var output = new MemoryStream();
        // Disposing the writer finalizes the RIFF sizes; MemoryStream.ToArray remains available.
        using (var writer = new WaveFileWriter(output, Format)) writer.Write(pcm, 0, pcm.Length);
        return output.ToArray();
    }

    internal static double Peak(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2))));
        return peak / 32768d;
    }

    /// <summary>Places timestamped packets without collapsing silent gaps; clips both ends to the requested interval.</summary>
    internal static void Place(byte[] target, byte[] packet, long frame)
    {
        long start = frame * BlockAlign;
        int source = (int)Math.Min(packet.Length, Math.Max(0, -start));
        int destination = (int)Math.Min(target.Length, Math.Max(0, start));
        int length = Math.Min(packet.Length - source, target.Length - destination);
        if (length > 0) Buffer.BlockCopy(packet, source, target, destination, length);
    }
}
