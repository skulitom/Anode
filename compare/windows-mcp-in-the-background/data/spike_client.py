"""Spike client: drive Windows-MCP (running inside the Anode seat) through seven tool calls over streamable HTTP.

ControlStatus -> App (launch_executable: the throwaway test pad) -> Snapshot -> Type -> Screenshot -> Snapshot ->
ControlStatus. Saves every raw result under run/ and prints a JSON summary, including whether any result mentions
USER_CONTROL or TAKEOVER_PENDING (the control gate tripping).

Usage: .venv\\Scripts\\python.exe spike_client.py http://127.0.0.1:8765/mcp
"""
import asyncio
import base64
import json
import os
import sys
import time

from fastmcp import Client

HERE = os.path.dirname(os.path.abspath(__file__))
RUN = os.path.join(HERE, "run")
LINE = "Typed by Windows-MCP inside an Anode seat."
# The test pad opens at (40,40), 640x400; its text box fills the client area.
BOX = [360, 260]
GATE_WORDS = ("USER_CONTROL", "TAKEOVER_PENDING")


def dump(result):
    """Turn a CallToolResult into JSON-safe parts; images go to files."""
    parts = []
    for i, c in enumerate(result.content or []):
        kind = getattr(c, "type", type(c).__name__)
        if kind == "image":
            parts.append({"type": "image", "mimeType": c.mimeType, "bytes": len(base64.b64decode(c.data))})
        else:
            parts.append({"type": kind, "text": getattr(c, "text", str(c))})
    return {"is_error": bool(result.is_error), "parts": parts, "data": result.data}


def save_images(result, stem):
    paths = []
    for i, c in enumerate(result.content or []):
        if getattr(c, "type", "") == "image":
            ext = "png" if "png" in c.mimeType else "jpg"
            p = os.path.join(RUN, f"{stem}-{i}.{ext}")
            with open(p, "wb") as f:
                f.write(base64.b64decode(c.data))
            paths.append(p)
    return paths


async def main(url):
    os.makedirs(RUN, exist_ok=True)
    steps = []
    pad = os.path.join(HERE, "testpad.ps1")
    plan = [
        ("ControlStatus", {}),
        ("App", {"mode": "launch_executable", "executable": r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                 "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", pad]}),
        ("Snapshot", {"use_vision": False}),
        ("Type", {"text": LINE, "loc": BOX, "clear": True}),
        ("Screenshot", {}),
        ("Snapshot", {"use_vision": False}),
        ("ControlStatus", {}),
    ]
    async with Client(url, timeout=120) as c:
        tools = sorted(t.name for t in await c.list_tools())
        for n, (name, args) in enumerate(plan, 1):
            t0 = time.time()
            try:
                r = await c.call_tool(name, args, raise_on_error=False)
                d = dump(r)
                d["images"] = save_images(r, f"{n:02d}-{name}")
            except Exception as e:  # keep going: a failed step is evidence too
                d = {"is_error": True, "exception": repr(e)}
            d.update({"step": n, "tool": name, "args": args, "seconds": round(time.time() - t0, 2)})
            steps.append(d)
            with open(os.path.join(RUN, f"{n:02d}-{name}.json"), "w", encoding="utf-8") as f:
                json.dump(d, f, indent=1, default=str)
            if name == "App":
                await asyncio.sleep(4)  # let the test pad window appear
    text = json.dumps(steps, default=str)
    summary = {
        "tools_listed": tools,
        "steps": [{"step": s["step"], "tool": s["tool"], "ok": not s.get("is_error"), "seconds": s["seconds"]} for s in steps],
        "gate_words_seen": [w for w in GATE_WORDS if w in text],
        "control_status_first": steps[0].get("data"),
        "control_status_last": steps[-1].get("data"),
    }
    with open(os.path.join(RUN, "client-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=1, default=str)
    print(json.dumps(summary, indent=1, default=str))


asyncio.run(main(sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8765/mcp"))
