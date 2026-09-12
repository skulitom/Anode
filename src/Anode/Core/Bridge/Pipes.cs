using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anode.Core.Util;

namespace Anode.Core.Bridge;

/// <summary>
/// Newline-delimited JSON over a named pipe. Both hops in Anode speak it:
/// CLI/MCP -> daemon, and daemon -> seat host. One JSON object per line, request
/// then response, in order, on a single connection.
/// </summary>
internal static class JsonLine
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string Serialize(JsonObject value) => value.ToJsonString(Compact);

    public static JsonObject? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static JsonObject Ok(JsonObject? result = null)
    {
        var response = new JsonObject { ["ok"] = true };
        if (result is not null) response["result"] = result;
        return response;
    }

    public static JsonObject Fail(string error)
    {
        return new JsonObject { ["ok"] = false, ["error"] = error };
    }

    // ---- typed readers that tolerate whatever an agent actually sends ----

    public static string? Str(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? n.ToString() : null;

    public static int? Int(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        try { return n.GetValue<int>(); }
        catch { return int.TryParse(n.ToString(), out int v) ? v : null; }
    }

    public static double? Num(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        try { return n.GetValue<double>(); }
        catch
        {
            return double.TryParse(n.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
        }
    }

    public static bool? Bool(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        try { return n.GetValue<bool>(); }
        catch { return bool.TryParse(n.ToString(), out bool v) ? v : null; }
    }

    public static JsonObject? Obj(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) ? n as JsonObject : null;
}

/// <summary>Serves newline-delimited JSON requests on a named pipe.</summary>
internal sealed class JsonPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<JsonObject, Task<JsonObject>> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptLoop;

    public JsonPipeServer(string pipeName, Func<JsonObject, Task<JsonObject>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException ex)
            {
                Log.Error($"pipe {_pipeName} could not be created", ex);
                await Task.Delay(500).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"pipe {_pipeName} accept failed", ex);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, JsonLine.Utf8, false, 8192, leaveOpen: true);
            using var writer = new StreamWriter(pipe, JsonLine.Utf8, 8192, leaveOpen: true) { AutoFlush = true };

            while (!_stopping.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
                if (line is null) break;

                var request = JsonLine.Parse(line);
                JsonObject response;
                if (request is null)
                {
                    response = JsonLine.Fail("request was not a JSON object");
                }
                else
                {
                    try
                    {
                        response = await _handler(request).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"handler for '{request.Str("op")}' threw", ex);
                        response = JsonLine.Fail($"{ex.GetType().Name}: {ex.Message}");
                    }

                    if (request.TryGetPropertyValue("id", out var id) && id is not null)
                    {
                        response["id"] = id.DeepClone();
                    }
                }

                await writer.WriteLineAsync(JsonLine.Serialize(response)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* client hung up */ }
        catch (Exception ex) { Log.Error($"pipe {_pipeName} session ended badly", ex); }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        try { _stopping.Cancel(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stopping.Dispose();
    }
}

/// <summary>Sends newline-delimited JSON requests to a <see cref="JsonPipeServer"/>.</summary>
internal sealed class JsonPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _nextId;

    private JsonPipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, JsonLine.Utf8, false, 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, JsonLine.Utf8, 8192, leaveOpen: true) { AutoFlush = true };
    }

    public bool IsConnected => _pipe.IsConnected;

    public static async Task<JsonPipeClient?> TryConnectAsync(string pipeName, int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Byte;
            return new JsonPipeClient(pipe);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async Task<JsonObject> RequestAsync(string op, JsonObject? args = null, int timeoutMs = 60_000)
    {
        var request = args is null ? new JsonObject() : (JsonObject)args.DeepClone();
        request["op"] = op;
        request["id"] = Interlocked.Increment(ref _nextId);

        await _oneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(timeoutMs);
            await _writer.WriteLineAsync(JsonLine.Serialize(request)).ConfigureAwait(false);
            string? line = await _reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null) return JsonLine.Fail("connection closed before a reply arrived");
            return JsonLine.Parse(line) ?? JsonLine.Fail("reply was not a JSON object");
        }
        catch (OperationCanceledException)
        {
            return JsonLine.Fail($"'{op}' timed out after {timeoutMs} ms");
        }
        catch (IOException ex)
        {
            return JsonLine.Fail($"connection lost during '{op}': {ex.Message}");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _oneAtATime.Dispose();
    }
}
