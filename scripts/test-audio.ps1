#requires -Version 5.1
<# Opt-in, audible test in an existing audio-enabled seat. Requires the caller's desktop lease.
   Never starts/stops a seat, opens its viewer, or reads a microphone/parent endpoint.
#>
param([string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\audio-test'))
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
function Invoke-AnodeJson([string[]]$Arguments) {
    $text = & $Anode @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Anode $($Arguments[0]) failed: $text" }
    $text | ConvertFrom-Json
}
$before = Invoke-AnodeJson @('status', '--json')
if ($before.state -ne 'ready' -or $before.session -eq $before.parentSession) { throw 'An existing ready child session is required.' }
# Renewal cannot start a daemon/seat or take another agent's desktop.
$null = Invoke-AnodeJson @('lease', 'renew', '--ttl', '120')
$audio = Invoke-AnodeJson @('audio', 'status', '--json')
if (-not $audio.available) { throw $audio.summary }
if ($audio.playbackState -in @('starting', 'playing')) { throw 'Existing agent audio must finish before this test.' }
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory -Force
$tone = Join-Path $runDirectory 'tone.wav'
$heard = Join-Path $runDirectory 'heard.wav'

if (-not ('AnodeAudioFixture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
public static class AnodeAudioFixture {
    public static void WriteTone(string path) {
        const int rate = 48000, seconds = 12, bytes = rate * seconds * 4;
        using (var output = new BinaryWriter(File.Create(path))) {
            output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + bytes);
            output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
            output.Write((short)1); output.Write((short)2); output.Write(rate); output.Write(rate * 4);
            output.Write((short)4); output.Write((short)16); output.Write(Encoding.ASCII.GetBytes("data")); output.Write(bytes);
            for (int i = 0; i < rate * seconds; i++) {
                double fade = Math.Min(1, Math.Min(i, rate * seconds - 1 - i) / 480.0);
                short sample = (short)(4000 * fade * Math.Sin(2 * Math.PI * 880 * i / rate));
                output.Write(sample); output.Write(sample);
            }
        }
    }
    public static double ToneAmplitude(string path) {
        using (var reader = new BinaryReader(File.OpenRead(path))) {
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new Exception("Not RIFF");
            reader.ReadInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new Exception("Not WAV");
            int rate = 0, channels = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length) {
                string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4)); int size = reader.ReadInt32();
                long next = reader.BaseStream.Position + size + (size & 1);
                if (chunk == "fmt ") {
                    if (reader.ReadInt16() != 1) throw new Exception("Not PCM");
                    channels = reader.ReadInt16(); rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16();
                    if (reader.ReadInt16() != 16 || channels != 2 || rate != 48000) throw new Exception("Unexpected capture format");
                }
                if (chunk == "data") {
                    if (rate == 0) throw new Exception("Missing format");
                    int count = size / (channels * 2); double real = 0, imaginary = 0;
                    for (int i = 0; i < count; i++) {
                        double sample = 0;
                        for (int c = 0; c < channels; c++) sample += reader.ReadInt16() / (32768.0 * channels);
                        double angle = 2 * Math.PI * 880 * i / rate;
                        real += sample * Math.Cos(angle); imaginary += sample * Math.Sin(angle);
                    }
                    return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
                }
                reader.BaseStream.Position = next;
            }
            throw new Exception("Missing samples");
        }
    }
}
'@
}
[AnodeAudioFixture]::WriteTone($tone)
$requested = $false
try {
    $requested = $true
    $null = Invoke-AnodeJson @('audio', 'play', $tone, '--json')
    $recorded = Invoke-AnodeJson @('audio', 'listen', $heard, '--ms', '3000', '--json')
    $amplitude = [AnodeAudioFixture]::ToneAmplitude($heard)
    if ($recorded.silent -or $recorded.durationMs -ne 3000 -or $amplitude -lt 0.01) {
        throw "The expected 880 Hz tone was not captured (amplitude $amplitude). See $heard"
    }
    $null = Invoke-AnodeJson @('audio', 'stop', '--json')
    $requested = $false
    $after = Invoke-AnodeJson @('status', '--json')
    if ($after.session -ne $before.session -or $after.viewerVisible -ne $before.viewerVisible) { throw 'Session or viewer state changed.' }
    [pscustomobject]@{ passed = $true; session = $before.session; toneAmplitude = $amplitude; recording = $heard;
        discontinuities = $recorded.discontinuities; timestampErrors = $recorded.timestampErrors; viewerUnchanged = $true
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $runDirectory 'result.json')
} finally {
    if ($requested) { & $Anode audio stop --json | Out-Null }
}
