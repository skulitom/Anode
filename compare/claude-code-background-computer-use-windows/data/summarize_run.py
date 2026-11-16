"""Turn one headless Claude Code job log (stream-json) into a small, publishable summary.

Keeps: Claude Code version, model, the MCP servers' status, each tool call in order (name, a short
scrubbed argument note, ok or error), counts, turns, duration, cost and the agent's final answer.
Drops: tool results, screenshots, thinking, session ids and the machine's own paths.

Usage: python summarize_run.py <job.jsonl> <workdir> <out.json>
"""
import json
import re
import sys


def scrub(text, workdir):
    for sep in ("\\\\", "\\", "/"):
        w = workdir.replace("/", sep).replace("\\", sep) if sep != "\\\\" else workdir.replace("/", "\\\\").replace("\\", "\\\\")
        text = re.sub(re.escape(w), "<workdir>", text, flags=re.I)
    text = re.sub(r"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\"' ]+", "<home>", text)
    return text


def note(name, inp, workdir):
    keep = {}
    for k, v in (inp or {}).items():
        if k in ("snapshotId", "windowId", "jobId", "after"):
            continue
        s = v if isinstance(v, str) else json.dumps(v)
        s = scrub(s, workdir)
        keep[k] = s if len(s) <= 160 else s[:157] + "..."
    return keep


def main():
    log, workdir, out = sys.argv[1:4]
    init, result, calls, by_id = {}, {}, [], {}
    with open(log, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            t = e.get("type")
            if t == "system" and e.get("subtype") == "init":
                init = e
            elif t == "assistant":
                for c in e.get("message", {}).get("content", []):
                    if c.get("type") == "tool_use":
                        call = {"n": len(calls) + 1, "tool": c["name"], "args": note(c["name"], c.get("input"), workdir), "result": "no result"}
                        calls.append(call)
                        by_id[c["id"]] = call
            elif t == "user":
                content = e.get("message", {}).get("content", [])
                if isinstance(content, list):
                    for c in content:
                        if c.get("type") == "tool_result" and c.get("tool_use_id") in by_id:
                            call = by_id[c["tool_use_id"]]
                            call["result"] = "error" if c.get("is_error") else "ok"
                            if c.get("is_error"):
                                body = c.get("content")
                                if isinstance(body, list):
                                    body = " ".join(b.get("text", "") for b in body if isinstance(b, dict))
                                body = scrub(" ".join(str(body or "").split()), workdir)
                                call["error_text"] = body if len(body) <= 240 else body[:237] + "..."
            elif t == "result":
                result = e
    counts = {}
    for c in calls:
        counts[c["tool"]] = counts.get(c["tool"], 0) + 1
    anode = [c for c in calls if c["tool"].startswith("mcp__anode__")]
    summary = {
        "claude_code_version": init.get("claude_code_version"),
        "model": init.get("model"),
        "permission_mode": init.get("permissionMode"),
        "mcp_servers": [{"name": s.get("name"), "status": s.get("status")} for s in init.get("mcp_servers", [])],
        "skills_or_plugins_loaded_from_user_config": "none (fresh, empty Claude Code config folder)",
        "tool_calls": len(calls),
        "anode_tool_calls": len(anode),
        "tool_calls_with_error": sum(1 for c in calls if c["result"] == "error"),
        "calls_by_tool": dict(sorted(counts.items())),
        "turns": result.get("num_turns"),
        "duration_s": round(result.get("duration_ms", 0) / 1000, 1),
        "api_time_s": round(result.get("duration_api_ms", 0) / 1000, 1),
        "cost_usd_list_price_estimate": round(result.get("total_cost_usd", 0), 4),
        "ended": result.get("subtype"),
        "calls": calls,
        "final_answer": scrub(result.get("result") or "", workdir),
    }
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(summary, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print(json.dumps({k: v for k, v in summary.items() if k not in ("calls", "final_answer")}, indent=1))


if __name__ == "__main__":
    main()
