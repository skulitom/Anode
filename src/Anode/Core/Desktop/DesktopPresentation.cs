using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Desktop;

internal static class DesktopPresentation
{
    private static string Line(string? value) => string.Concat((value ?? "").Select(c => char.IsControl(c) ? ' ' : c));
    private static string State(JsonObject node)
    {
        var parts = new List<string>();
        if (node.Bool("focused") == true) parts.Add("focused");
        if (node.Bool("enabled") == false) parts.Add("disabled");
        if (node.Bool("offscreen") == true) parts.Add("offscreen");
        if (node.Bool("readOnly") == true) parts.Add("read-only");
        if (node.Str("toggleState") is { } toggle) parts.Add("toggle: " + toggle);
        if (node.Bool("selected") is bool selected) parts.Add(selected ? "selected" : "not selected");
        if (node.Str("expandState") is { } expanded) parts.Add(expanded);
        if (node.Obj("range") is { } range) parts.Add($"value: {range["value"]} ({range["minimum"]}–{range["maximum"]})");
        if (node.Obj("scroll") is { } scroll) parts.Add($"scroll: horizontal {scroll["horizontalPercent"]}%, vertical {scroll["verticalPercent"]}%");
        return Line(string.Join("; ", parts));
    }
    public static string Summary(JsonObject result)
    {
        var text = new StringBuilder();
        if (result["windows"] is JsonArray windows)
        {
            text.AppendLine($"{windows.Count} seat window(s). Use windowId with seat_observe.");
            foreach (var window in windows.OfType<JsonObject>())
                text.AppendLine($"{window.Str("windowId")}  {Line(window.Str("process"))} (pid {window.Int("pid")})  {Line(window.Str("title"))}"
                    + (window.Bool("foreground") == true ? " [foreground]" : "") + (window.Bool("minimized") == true ? " [minimized]" : ""));
        }
        else if (result["elements"] is JsonArray elements)
        {
            text.AppendLine($"{Line(result.Obj("window")?.Str("title"))} — {result.Str("windowId")}");
            text.AppendLine(result.Str("snapshotId") is { } snapshot
                ? $"Snapshot {snapshot}; expires in 90 seconds and is consumed by an action."
                : "Diagnostic observation only; call seat_observe before taking control actions.");
            foreach (var node in elements.OfType<JsonObject>())
            {
                text.Append(' ', Math.Min(node.Int("depth") ?? 0, 12) * 2);
                text.Append($"[{node.Str("id")}] {node.Str("role")} \"{Line(node.Str("name"))}\"");
                if (State(node) is { Length: > 0 } state) text.Append(" [" + state + "]");
                if (node.Bool("password") == true) text.Append(" [password: content omitted]");
                if (node["actions"] is JsonArray actions && actions.Count > 0) text.Append(" {" + string.Join(", ", actions) + "}");
                text.AppendLine();
                if (node.Str("text") is { Length: > 0 } content) text.AppendLine("    text: " + Line(content));
                if (node.Str("selectedText") is { Length: > 0 } selected) text.AppendLine("    selected: " + Line(selected));
            }
            if (result.Bool("truncated") == true) text.AppendLine("Tree truncated; adjust maxElements/maxDepth or inspect a smaller window.");
            if (result.Str("screenshotError") is { } error) text.AppendLine(Line(error));
            if (result["warnings"] is JsonArray warnings)
                foreach (var warning in warnings) text.AppendLine(Line(warning?.ToString()));
        }
        else text.Append(result.Str("note") ?? "ok");
        return text.ToString().TrimEnd();
    }

    public static string Html(JsonObject result)
    {
        string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var rows = new StringBuilder();
        foreach (var node in (result["elements"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var bounds = node.Obj("bounds");
            rows.Append($"<tr data-x='{bounds?.Int("x") ?? 0}' data-y='{bounds?.Int("y") ?? 0}' data-w='{bounds?.Int("width") ?? 0}' data-h='{bounds?.Int("height") ?? 0}'>");
            rows.Append($"<td><code>{E(node.Str("id"))}</code></td><td>{E(node.Str("role"))}</td>");
            rows.Append($"<td><strong>{E(node.Str("name"))}</strong><div class='muted'>{E(node.Str("automationId"))}</div><pre>{E(node.Str("text"))}</pre></td>");
            rows.Append($"<td>{E(node.Bool("password") == true ? "Password content omitted" : string.Join(", ", node["actions"] as JsonArray ?? new()))}");
            rows.Append($"<div><span>{E(State(node))}</span></div>");
            rows.Append("</td></tr>");
        }
        var shot = result.Obj("screenshot");
        string picture = shot?.Str("data") is { } data
            ? $"<div id='screen'><img alt='Seat screenshot' src='data:image/png;base64,{E(data)}'><div id='highlight'></div></div>"
            : $"<div class='notice'>{E(result.Str("screenshotError") ?? "Screenshot not requested.")}</div>";
        return """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Anode — seat inspection</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#12171e;color:#e5edf5;font:15px system-ui,sans-serif}
main{max-width:1450px;margin:auto;padding:32px}h1{font-size:28px;margin:0 0 10px}p,.muted{color:#a6b6c8}.bar{display:flex;gap:16px;align-items:center;margin:24px 0}
input{padding:12px;background:#202a36;color:white;border:1px solid #496077;border-radius:8px;width:100%}table{border-collapse:collapse;width:100%;background:#18212c;table-layout:fixed}th:first-child{width:65px}th:nth-child(2){width:120px}th:last-child{width:25%}td{overflow-wrap:anywhere}@media(max-width:700px){main{padding:16px}th,td{padding:8px}th:nth-child(2){width:90px}}
th,td{padding:12px;text-align:left;border-bottom:1px solid #2c3a4b;vertical-align:top}th{color:#a6b6c8;font-weight:500}tr:hover{background:#243449}pre{white-space:pre-wrap;word-break:break-word;margin:8px 0;font:13px ui-monospace,monospace}
code,span{color:#7ed9ca}#screen{position:relative;width:100%;max-width:1280px;margin:24px auto}img{width:100%;display:block;border:1px solid #496077;border-radius:8px}#highlight{display:none;position:absolute;border:3px solid #69f0ae;background:#69f0ae33;pointer-events:none}.notice{padding:20px;background:#302818;border:1px solid #806a37;border-radius:8px}
</style><main>
""" + $"<h1>{E(result.Obj("window")?.Str("title"))}</h1><p>{E(result.Str("windowId"))} · {E(result.Str("observedAt"))} · Read-only observation; actions require fresh Anode references.</p>"
            + picture + (result.Bool("truncated") == true ? "<p>Partial tree: the configured node, depth, or time limit was reached.</p>" : "")
            + "<div class='bar'><input id='filter' placeholder='Filter controls by name, role, text or action' aria-label='Filter controls'></div><table><thead><tr><th>ID</th><th>Role</th><th>Control and text</th><th>Actions / state</th></tr></thead><tbody>"
            + rows + "</tbody></table></main><script>"
            + $"const sw={shot?.Int("sourceWidth") ?? 1},sh={shot?.Int("sourceHeight") ?? 1};"
            + """
const rows=[...document.querySelectorAll('tbody tr')],mark=document.querySelector('#highlight');
document.querySelector('#filter').addEventListener('input',e=>{const q=e.target.value.toLowerCase();rows.forEach(r=>r.hidden=!r.textContent.toLowerCase().includes(q))});
rows.forEach(r=>{r.addEventListener('mouseenter',()=>{if(!mark)return;Object.assign(mark.style,{display:'block',left:(100*r.dataset.x/sw)+'%',top:(100*r.dataset.y/sh)+'%',width:(100*r.dataset.w/sw)+'%',height:(100*r.dataset.h/sh)+'%'})});r.addEventListener('mouseleave',()=>{if(mark)mark.style.display='none'})});
</script></html>
""";
    }
}
