"""Sample the parent (console) desktop while Windows-MCP runs in the Anode seat.

Records, every --interval seconds: cursor position, foreground window (handle, pid, title length)
and the parent session's input idle time (GetLastInputInfo), so a cursor move can be told
apart from the user's own input. Writes a CSV and prints a JSON summary.

Usage: python parent_watch.py --seconds 180 --out parent.csv
Stop early with Ctrl+C or by creating the file named by --stop-file.
"""
import argparse
import csv
import ctypes
import ctypes.wintypes as wt
import json
import os
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("dwTime", wt.DWORD)]


def cursor():
    p = wt.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def foreground():
    h = user32.GetForegroundWindow()
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    # Only the title's length is kept: the user's window titles stay out of the evidence.
    return int(h or 0), int(pid.value), user32.GetWindowTextLengthW(h)


def idle_ms():
    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(LASTINPUTINFO)
    user32.GetLastInputInfo(ctypes.byref(li))
    return (kernel32.GetTickCount() - li.dwTime) & 0xFFFFFFFF


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=180)
    ap.add_argument("--interval", type=float, default=0.05)
    ap.add_argument("--out", default="parent.csv")
    ap.add_argument("--stop-file", default="parent.stop")
    a = ap.parse_args()
    sid = wt.DWORD()
    kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(sid))
    rows, t0 = [], time.time()
    try:
        while time.time() - t0 < a.seconds and not os.path.exists(a.stop_file):
            x, y = cursor()
            h, pid, title = foreground()
            rows.append((round(time.time() - t0, 3), x, y, h, pid, title, idle_ms()))
            time.sleep(a.interval)
    except KeyboardInterrupt:
        pass
    with open(a.out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["t_s", "x", "y", "hwnd", "pid", "title_len", "idle_ms"])
        w.writerows(rows)
    positions = sorted({(r[1], r[2]) for r in rows})
    fg = []
    for r in rows:
        if not fg or fg[-1]["hwnd"] != r[3]:
            fg.append({"t_s": r[0], "hwnd": r[3], "pid": r[4], "title_len": r[5]})
    # A cursor change while the parent session's idle time keeps growing was not caused by its own user.
    unexplained = []
    for prev, cur in zip(rows, rows[1:]):
        if (prev[1], prev[2]) != (cur[1], cur[2]) and cur[6] >= prev[6]:
            unexplained.append({"t_s": cur[0], "from": prev[1:3], "to": cur[1:3], "idle_ms": cur[6]})
    print(json.dumps({
        "session": sid.value, "samples": len(rows), "seconds": round(time.time() - t0, 1),
        "distinct_cursor_positions": len(positions), "first_position": rows[0][1:3] if rows else None,
        "foreground_changes": len(fg) - 1, "foreground": fg[:10],
        "min_idle_ms": min((r[6] for r in rows), default=None),
        "cursor_moves_without_parent_input": unexplained[:20], "out": a.out,
    }, indent=1))


if __name__ == "__main__":
    main()
