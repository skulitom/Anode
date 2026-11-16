"""Watch the PC's own desktop (the parent session) while an agent tests an app in the Anode seat.

Every --interval seconds it records: the pointer position, the foreground window (handle and pid),
whether that foreground window belongs to the app under test or to Anode, and the session's input
idle time (GetLastInputInfo). Once a second it also records which Windows sessions hold a process
of the app under test, and how many of that app's windows exist on this desktop.

It writes a CSV (kept private: it holds the pointer path of whoever is at the PC) and prints a JSON
summary of counts only. The user's window titles and program names are never read.

Usage: python parent_watch.py --app TipCalc.exe --seconds 600 --out parent.csv --stop-file parent.stop
Stop early by creating the file named by --stop-file.
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

TH32CS_SNAPPROCESS = 0x2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.UINT), ("dwTime", wt.DWORD)]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", wt.LONG), ("dwFlags", wt.DWORD), ("szExeFile", wt.WCHAR * 260)]


def cursor():
    p = wt.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def idle_ms():
    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(LASTINPUTINFO)
    user32.GetLastInputInfo(ctypes.byref(li))
    return (kernel32.GetTickCount() - li.dwTime) & 0xFFFFFFFF


_names = {}


def exe_name(pid):
    """Base name of a process image, cached. Only compared with the app's and Anode's names; never stored."""
    if pid in _names:
        return _names[pid]
    name = ""
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if h:
        buf = ctypes.create_unicode_buffer(1024)
        n = wt.DWORD(1024)
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
            name = os.path.basename(buf.value).lower()
        kernel32.CloseHandle(h)
    _names[pid] = name
    return name


def foreground(app):
    h = user32.GetForegroundWindow()
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    name = exe_name(pid.value) if pid.value else ""
    return int(h or 0), int(pid.value), int(name == app), int(name.startswith("anode"))


def app_processes(app):
    """(pid, session) of every running process whose image name is the app under test."""
    out = []
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32W()
    e.dwSize = ctypes.sizeof(PROCESSENTRY32W)
    ok = kernel32.Process32FirstW(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile.lower() == app:
            sid = wt.DWORD()
            if kernel32.ProcessIdToSessionId(e.th32ProcessID, ctypes.byref(sid)):
                out.append((int(e.th32ProcessID), int(sid.value)))
        ok = kernel32.Process32NextW(snap, ctypes.byref(e))
    kernel32.CloseHandle(snap)
    return out


def app_windows_here(pids):
    """Top-level windows on THIS desktop that belong to one of the app's processes."""
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids:
            found.append(int(hwnd))
        return True

    user32.EnumWindows(cb, 0)
    return len(found)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", default="TipCalc.exe")
    ap.add_argument("--seconds", type=float, default=600)
    ap.add_argument("--interval", type=float, default=0.05)
    ap.add_argument("--out", default="parent.csv")
    ap.add_argument("--stop-file", default="parent.stop")
    a = ap.parse_args()
    app = a.app.lower()
    sid = wt.DWORD()
    kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(sid))
    rows, procs, t0, last_proc = [], [], time.time(), -10.0
    while time.time() - t0 < a.seconds and not os.path.exists(a.stop_file):
        t = time.time() - t0
        x, y = cursor()
        h, pid, is_app, is_anode = foreground(app)
        rows.append((round(t, 3), x, y, h, pid, is_app, is_anode, idle_ms()))
        if t - last_proc >= 1.0:
            last_proc = t
            ps = app_processes(app)
            procs.append((round(t, 1), sorted({s for _, s in ps}), app_windows_here({p for p, _ in ps})))
        time.sleep(a.interval)
    with open(a.out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["t_s", "x", "y", "fg_hwnd", "fg_pid", "fg_is_app", "fg_is_anode", "idle_ms"])
        w.writerows(rows)
    fg_changes = sum(1 for p, c in zip(rows, rows[1:]) if p[3] != c[3])
    # The idle clock restarts whenever any input, real or injected, reaches this session.
    input_bursts = sum(1 for p, c in zip(rows, rows[1:]) if c[7] < p[7])
    # A pointer move is explained when some input reached this session since the previous sample, that is,
    # when the idle clock reads no more than the gap between the two samples (plus two 16 ms clock ticks).
    # A move with an older last input was not caused by input here (a program placed the pointer).
    unexplained = sum(1 for p, c in zip(rows, rows[1:])
                      if (p[1], p[2]) != (c[1], c[2]) and c[7] > (c[0] - p[0]) * 1000 + 32)
    sessions = sorted({s for _, ss, _ in procs for s in ss})
    print(json.dumps({
        "app": a.app, "watcher_session": sid.value, "samples": len(rows),
        "seconds": round(rows[-1][0], 1) if rows else 0, "interval_s": a.interval,
        "foreground_samples_app_under_test": sum(r[5] for r in rows),
        "foreground_samples_anode": sum(r[6] for r in rows),
        "foreground_changes": fg_changes,
        "distinct_pointer_positions": len({(r[1], r[2]) for r in rows}),
        "pointer_moves_without_input_in_this_session": unexplained,
        "input_bursts_in_this_session": input_bursts,
        "longest_idle_ms": max((r[7] for r in rows), default=None),
        "process_checks": len(procs),
        "process_checks_with_app_running": sum(1 for _, ss, _ in procs if ss),
        "sessions_holding_app_process": sessions,
        "max_app_windows_on_this_desktop": max((n for _, _, n in procs), default=0),
    }, indent=1))


if __name__ == "__main__":
    main()
