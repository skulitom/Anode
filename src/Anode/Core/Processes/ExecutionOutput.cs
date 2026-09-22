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
    public ExecutionOutput(int capacity = 131072)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }
    public void Append(string stream, string text)
    {
        if (text.Length == 0) return;
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
                if (char.IsSurrogatePair(last.Text, removed - 1)) removed++;
                _chunks.Enqueue(last with { Start = last.Start + removed, Text = last.Text[removed..] });
                _size = last.Text.Length - removed;
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
                int skip = (int)Math.Clamp(cursor - chunk.Start, 0, chunk.Text.Length);
                if (skip == chunk.Text.Length) continue;
                if (skip > 0 && char.IsSurrogatePair(chunk.Text, skip - 1))
                    throw new ArgumentException("after splits a Unicode character; use a cursor returned by this job.");
                if (remaining <= 0) break;
                // maxChars counts Unicode scalar values. Cursors remain opaque UTF-16
                // offsets, so even a one-character page can carry a complete emoji.
                int length = 0, characters = 0;
                while (skip + length < chunk.Text.Length && characters < remaining)
                {
                    length += char.IsSurrogatePair(chunk.Text, skip + length) ? 2 : 1;
                    characters++;
                }
                if (length <= 0) continue;
                (chunk.Stream == "stdout" ? stdout : stderr).Append(chunk.Text, skip, length);
                cursor = chunk.Start + skip + length;
                remaining -= characters;
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
