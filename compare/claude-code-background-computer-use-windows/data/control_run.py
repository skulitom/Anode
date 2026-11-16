"""Positive control for parent_watch.py: does it count a pointer move that no input caused?

Run this INSIDE the seat (through Anode), never on a desktop someone is using: it starts the watcher there,
then places the seat's pointer at five spots with SetCursorPos (a program moving the pointer, with no input),
puts it back, and prints the watcher's summary. The watcher must report at least six moves without input.

Usage: python control_run.py <folder for the watcher's csv>
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
here = os.path.dirname(os.path.abspath(__file__))
out = sys.argv[1]
p = wt.POINT()
user32.GetCursorPos(ctypes.byref(p))
watch = subprocess.Popen([sys.executable, "-I", os.path.join(here, "parent_watch.py"), "--app", "none.exe",
                          "--seconds", "5", "--out", os.path.join(out, "control.csv"),
                          "--stop-file", os.path.join(out, "control.stop")], stdout=subprocess.PIPE, text=True)
time.sleep(1.0)
spots = [(100, 100), (300, 120), (500, 300), (700, 200), (900, 400)]
for x, y in spots:
    user32.SetCursorPos(x, y)
    time.sleep(0.4)
user32.SetCursorPos(p.x, p.y)
summary, _ = watch.communicate(timeout=30)
print("scripted pointer placements:", len(spots) + 1)
print(summary)
