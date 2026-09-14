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
    private readonly object _pipeGate = new();
    private readonly HashSet<NamedPipeServerStream> _connections = new();
    private bool _disposed;
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
                lock (_pipeGate)
                {
                    if (_disposed) return;
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    _connections.Add(pipe);
                }
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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                CloseConnection(pipe);
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"pipe {_pipeName} accept failed", ex);
                CloseConnection(pipe);
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
        catch (ObjectDisposedException) { }
        catch (IOException) { /* client hung up */ }
        catch (Exception ex) { Log.Error($"pipe {_pipeName} session ended badly", ex); }
        finally
        {
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
            CloseConnection(pipe);
        }
    }

    private void CloseConnection(NamedPipeServerStream pipe)
    {
        lock (_pipeGate) _connections.Remove(pipe);
        pipe.Dispose();
    }

    public void Dispose()
    {
        NamedPipeServerStream[] connections;
        lock (_pipeGate)
        {
            if (_disposed) return;
            _disposed = true;
            connections = _connections.ToArray();
        }
        try { _stopping.Cancel(); } catch { }
        foreach (var pipe in connections) CloseConnection(pipe);
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        // Active handlers may still unwind. Their cancellation source is managed
        // state and can be collected once the handlers release the server.
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
    private int _closed;

    private JsonPipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, JsonLine.Utf8, false, 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, JsonLine.Utf8, 8192, leaveOpen: true) { AutoFlush = true };
    }

    public bool IsConnected => Volatile.Read(ref _closed) == 0 && _pipe.IsConnected;

    public static async Task<JsonPipeClient?> TryConnectAsync(string pipeName, int timeoutMs, CancellationToken cancel = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMs, cancel).ConfigureAwait(false);
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
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonObject> RequestAsync(string op, JsonObject? args = null, int timeoutMs = 60_000, CancellationToken cancel = default)
    {
        var request = args is null ? new JsonObject() : (JsonObject)args.DeepClone();
        request["op"] = op;
        int id = Interlocked.Increment(ref _nextId);
        request["id"] = id;

        // Include queueing and writes in the deadline. In particular, a stop request
        // must not wait indefinitely behind a long-running operation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeoutMs);
        bool entered = false;
        bool sent = false;
        try
        {
            await _oneAtATime.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (!IsConnected) return JsonLine.Fail("connection is closed; reconnect before sending another request");
            sent = true;
            await _writer.WriteLineAsync(JsonLine.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
            string? line = await _reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null) throw new IOException("connection closed before a reply arrived");
            var response = JsonLine.Parse(line) ?? throw new IOException("reply was not a JSON object");
            if (response.Int("id") != id) throw new IOException("reply did not match the request id");
            return response;
        }
        catch (OperationCanceledException)
        {
            // Once a request is on the wire, a late reply would otherwise be read
            // as the next request's result. Do not reuse that connection or replay
            // the command: it may already have changed something in the seat.
            if (sent) Close();
            cancel.ThrowIfCancellationRequested();
            return JsonLine.Fail($"'{op}' timed out after {timeoutMs} ms");
        }
        catch (IOException ex)
        {
            Close();
            return JsonLine.Fail($"connection lost during '{op}': {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            Close();
            return JsonLine.Fail($"connection closed during '{op}'");
        }
        finally
        {
            if (entered) _oneAtATime.Release();
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _pipe.Dispose();
    }

    // Closing the pipe interrupts in-flight I/O. Leave the managed reader, writer
    // and semaphore for GC so concurrent requests can unwind and release the gate.
    public void Dispose() => Close();
}
