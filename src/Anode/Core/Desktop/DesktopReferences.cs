using System.Text.Json.Nodes;

namespace Anode.Core.Desktop;

/// <summary>Bounded, expiring references; no HWND or UIA object crosses the public tool boundary.</summary>
internal sealed class DesktopReferences
{
    private sealed record Entry<T>(T Value, DateTime Expires);
    private readonly Dictionary<string, Entry<WindowTarget>> _windows = new();
    private readonly Dictionary<string, Entry<JsonObject>> _observations = new();
    private readonly Func<DateTime> _now;
    public DesktopReferences(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public string RememberWindow(WindowTarget target)
    {
        Prune(_windows, 256);
        string? existing = _windows.FirstOrDefault(pair => pair.Value.Value == target).Key;
        string id = existing ?? "w_" + Guid.NewGuid().ToString("N")[..12];
        _windows[id] = new(target, _now().AddMinutes(10));
        return id;
    }

    public WindowTarget Window(string id) => _windows.TryGetValue(id, out var value) && value.Expires > _now()
        ? value.Value : throw new InvalidOperationException("Unknown or expired windowId. Call seat_windows again.");

    public string RememberObservation(WindowTarget target, JsonArray elements)
    {
        Prune(_observations, 16);
        string id = "s_" + Guid.NewGuid().ToString("N");
        _observations[id] = new(new JsonObject { ["target"] = target.ToJson(), ["elements"] = elements.DeepClone() }, _now().AddSeconds(90));
        return id;
    }

    public (WindowTarget Window, JsonObject Element) TakeElement(string snapshotId, string elementId)
    {
        if (!_observations.Remove(snapshotId, out var snapshot) || snapshot.Expires <= _now())
            throw new InvalidOperationException("Unknown, expired, or already used snapshotId. Inspect the window again before acting.");
        var element = snapshot.Value["elements"]!.AsArray().OfType<JsonObject>()
            .FirstOrDefault(value => value["id"]?.GetValue<string>() == elementId)
            ?? throw new ArgumentException("That elementId does not belong to this observation. Inspect again.");
        return (WindowTarget.FromJson(snapshot.Value["target"]!.AsObject()), (JsonObject)element.DeepClone());
    }

    public void InvalidateObservations() => _observations.Clear();
    public void Clear() { _windows.Clear(); _observations.Clear(); }

    private void Prune<T>(Dictionary<string, Entry<T>> entries, int limit)
    {
        foreach (string key in entries.Where(pair => pair.Value.Expires <= _now()).Select(pair => pair.Key).ToArray()) entries.Remove(key);
        while (entries.Count >= limit) entries.Remove(entries.MinBy(pair => pair.Value.Expires).Key);
    }
}
