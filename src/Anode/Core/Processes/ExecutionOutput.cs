using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Anode.Core.Processes;

/// <summary>Bounded stdout/stderr history with an absolute cursor that exposes dropped data.</summary>
internal sealed class ExecutionOutput
{
    private sealed record Chunk(long Start, string Stream, string Text);
    private readonly Queue<Chunk> _chunks = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _end;
    private int _size;
    public ExecutionOutput(int capacity = 131072) => _capacity = capacity;
    public void Append(string stream, string text)
    {
        lock (_gate)
        {
            _chunks.Enqueue(new Chunk(_end, stream, text));
            _end += text.Length;
            _size += text.Length;
            while (_size > _capacity && _chunks.Count > 1) _size -= _chunks.Dequeue().Text.Length;
            if (_size > _capacity)
            {
                var last = _chunks.Dequeue();
                int removed = last.Text.Length - _capacity;
                _chunks.Enqueue(last with { Start = last.Start + removed, Text = last.Text[removed..] });
                _size = _capacity;
            }
        }
    }
    public JsonObject Read(string? after, int maximum)
    {
        if (!long.TryParse(after ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out long cursor))
            throw new ArgumentException("after must be a cursor returned by this job.");
        lock (_gate)
        {
            if (cursor > _end) throw new ArgumentException("after is beyond this job's output; use its previous cursor.");
            long first = _chunks.TryPeek(out var oldest) ? oldest.Start : _end;
            bool lost = cursor < first;
            cursor = Math.Max(cursor, first);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            int remaining = maximum;
            foreach (var chunk in _chunks)
            {
                if (remaining <= 0) break;
                int skip = (int)Math.Clamp(cursor - chunk.Start, 0, chunk.Text.Length);
                int length = Math.Min(remaining, chunk.Text.Length - skip);
                if (length <= 0) continue;
                (chunk.Stream == "stdout" ? stdout : stderr).Append(chunk.Text, skip, length);
                cursor = chunk.Start + skip + length;
                remaining -= length;
            }
            return new JsonObject
            {
                ["stdout"] = stdout.ToString(), ["stderr"] = stderr.ToString(),
                ["cursor"] = cursor.ToString(CultureInfo.InvariantCulture),
                ["outputTruncated"] = lost, ["hasMoreOutput"] = cursor < _end
            };
        }
    }
}
