"""Regenerate Anode's raster brand assets.

Usage: python assets/make-assets.py [output-directory]

Needs Python 3 and Pillow. Reads only the geometry below and Segoe UI from %WINDIR%\\Fonts;
no network. Writes anode-512.png, anode.ico and social-preview.png next to this script unless
an output directory is given. anode.svg is the master geometry written by hand; change both
together.

The mark is the agent's seat: an amber screen with its own pointer. The near-black outline keeps
it visible on light taskbars and the amber fill on dark ones. Every size is drawn at 8x and
box-filtered, which leaves no ringing halo around the outline. Frames up to 48 px snap to the
pixel grid first so the outline stays solid, and the 16, 20 and 24 px tray frames use hand-drawn
pixel pointers.
"""

import io
import os
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ACCENT = (255, 163, 26, 255)   # #ffa31a
INK = (24, 24, 27, 255)        # #18181b, the viewer background
HALF = (140, 94, 27, 255)      # ink over accent at 50%, for pixel-art edges
MUTED = (161, 161, 170, 255)   # #a1a1aa, the viewer status text
PAPER = (250, 250, 250, 255)   # #fafafa

# Master geometry in the 256 x 256 viewBox of anode.svg.
SCREEN = (8, 24, 248, 232)     # outer box: x0, y0, x1, y1
SCREEN_RADIUS = 36
OUTLINE = 16
POINTER = [(88, 64), (88, 179), (117, 150), (140, 202), (162, 192), (141, 145), (169, 145)]
POINTER_HEIGHT = 138           # tip to tail end

# Tray sizes redrawn on the pixel grid: screen box, outline and corner radius in pixels, then the
# pointer's tip and its pixels ("#" ink, "+" half ink).
HINTS = {
    16: ((0, 1, 16, 15), 1, 2.5, (5, 3), [
        "#",
        "##",
        "###",
        "####",
        "#####",
        "######",
        "##+##",
        "#  ##",
        "    ##",
        "    ##",
    ]),
    20: ((1, 2, 19, 18), 1, 3, (7, 4), [
        "#",
        "##",
        "###",
        "####",
        "#####",
        "######",
        "#######",
        "###+##",
        "##  ##",
        "#    ##",
        "     ##",
        "      +",
    ]),
    24: ((1, 2, 23, 22), 2, 4.5, (8, 5), [
        "#",
        "##",
        "###",
        "####",
        "#####",
        "######",
        "#######",
        "########",
        "#########",
        "####+###",
        "###  ##",
        "##   ###",
        "#     ##",
        "      ##",
    ]),
}

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SUPERSAMPLE = 8
FONTS = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"


def pointer_at(tip, height):
    k = height / POINTER_HEIGHT
    ox, oy = POINTER[0]
    return [(tip[0] + (x - ox) * k, tip[1] + (y - oy) * k) for x, y in POINTER]


def mark_geometry(size):
    """Screen box, outline, corner radius and pointer polygon (None when hinted), in pixels."""
    if size in HINTS:
        screen, outline, radius, _, _ = HINTS[size]
        return screen, outline, radius, None
    k = size / 256
    snap = size <= 48
    fit = (lambda v: round(v * k)) if snap else (lambda v: v * k)
    screen = tuple(fit(v) for v in SCREEN)
    outline = max(1, round(OUTLINE * k)) if snap else OUTLINE * k
    tip = tuple(fit(v) for v in POINTER[0])
    return screen, outline, SCREEN_RADIUS * k, pointer_at(tip, POINTER_HEIGHT * k)


def fill_box(draw, box, radius, fill, s):
    x0, y0, x1, y1 = box
    draw.rounded_rectangle((x0 * s, y0 * s, x1 * s - 1, y1 * s - 1), radius=radius * s, fill=fill)


def fill_polygon(draw, points, fill, s):
    # Pillow fills every pixel a polygon touches, so it grows one supersampled pixel to the right
    # and bottom; the pointer's long left edge and tip stay exact.
    draw.polygon([(x * s, y * s) for x, y in points], fill=fill)


def draw_mark(draw, size, s, offset=(0, 0)):
    """Draw the mark for a `size` px icon onto `draw`, which is `s` times larger than the target."""
    (x0, y0, x1, y1), w, r, pointer = mark_geometry(size)
    dx, dy = offset
    fill_box(draw, (x0 + dx, y0 + dy, x1 + dx, y1 + dy), r, INK, s)
    fill_box(draw, (x0 + w + dx, y0 + w + dy, x1 - w + dx, y1 - w + dy), max(0, r - w), ACCENT, s)
    if pointer:
        fill_polygon(draw, [(x + dx, y + dy) for x, y in pointer], INK, s)


def render_mark(size):
    s = SUPERSAMPLE
    image = Image.new("RGBA", (size * s, size * s), (0, 0, 0, 0))
    draw_mark(ImageDraw.Draw(image), size, s)
    image = image.reduce(s)
    if size in HINTS:
        _, _, _, (tx, ty), rows = HINTS[size]
        for j, row in enumerate(rows):
            for i, c in enumerate(row):
                if c != " ":
                    image.putpixel((tx + i, ty + j), INK if c == "#" else HALF)
    return image


def ico_bytes(frames):
    """Icon file with 32-bit BMP frames below 256 px and a PNG frame at 256 px."""
    blobs = []
    for image in frames:
        n = image.width
        if n >= 256:
            buffer = io.BytesIO()
            image.save(buffer, "PNG", optimize=True)
            blobs.append(buffer.getvalue())
            continue
        header = struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0)
        pixels = bytearray()
        mask = bytearray()
        stride = (n + 31) // 32 * 4
        for y in range(n - 1, -1, -1):
            bits = bytearray(stride)
            for x in range(n):
                r, g, b, a = image.getpixel((x, y))
                pixels += bytes((b, g, r, a))
                if a == 0:
                    bits[x // 8] |= 0x80 >> (x % 8)
            mask += bits
        blobs.append(header + bytes(pixels) + bytes(mask))
    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    for image, blob in zip(frames, blobs):
        n = image.width % 256
        out += struct.pack("<BBBBHHII", n, n, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    return out + b"".join(blobs)


def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size)


def pane(d, box, frame, inside, s):
    """A screen with a title strip, separated from what lies behind it by an ink gap."""
    x0, y0, x1, y1 = box
    fill_box(d, (x0 - 8, y0 - 8, x1 + 8, y1 + 8), 28, INK, s)
    fill_box(d, box, 20, frame, s)
    fill_box(d, (x0 + 6, y0 + 46, x1 - 6, y1 - 6), 14, inside, s)


def window(d, box, body, bar, s):
    x0, y0, x1, y1 = box
    fill_box(d, box, 8, body, s)
    fill_box(d, (x0, y0, x1, y0 + 16), 8, bar, s)
    fill_box(d, (x0, y0 + 8, x1, y0 + 16), 0, bar, s)


def social_preview():
    """1280 x 640 repository card; key content stays inside x 96-1184 and y 112-532."""
    width, height, s = 1280, 640, 4
    zinc = {300: (212, 212, 216, 255), 500: (113, 113, 122, 255), 600: (82, 82, 91, 255),
            700: (63, 63, 70, 255), 800: (39, 39, 42, 255)}
    art = Image.new("RGBA", (width * s, height * s), INK)
    d = ImageDraw.Draw(art)

    # Motif: the agent seat runs behind your desktop, with its own pointer.
    seat = (872, 120, 1184, 338)
    desk = (768, 308, 1064, 524)
    pane(d, seat, ACCENT, (255, 200, 120, 255), s)
    window(d, (seat[0] + 28, seat[1] + 72, seat[0] + 200, seat[1] + 176), PAPER, INK, s)
    fill_polygon(d, pointer_at((seat[0] + 196, seat[1] + 110), 76), INK, s)
    pane(d, desk, zinc[600], zinc[800], s)
    window(d, (desk[0] + 26, desk[1] + 72, desk[0] + 186, desk[1] + 162), zinc[700], zinc[500], s)
    window(d, (desk[0] + 124, desk[1] + 116, desk[2] - 26, desk[3] - 22), zinc[700], zinc[500], s)
    fill_polygon(d, pointer_at((desk[0] + 90, desk[1] + 118), 58), PAPER, s)

    draw_mark(d, 96, s, offset=(96, 122))
    image = art.reduce(s)

    t = ImageDraw.Draw(image)
    t.text((208, 170), "Anode", font=font("seguisb.ttf", 88), fill=PAPER, anchor="lm")
    head = font("seguisb.ttf", 44)
    t.text((96, 314), "Background Windows desktop", font=head, fill=PAPER, anchor="ls")
    t.text((96, 368), "for AI agents", font=head, fill=PAPER, anchor="ls")
    body = font("segoeui.ttf", 26)
    t.text((96, 446), "Native GUI automation, screenshots and", font=body, fill=MUTED, anchor="ls")
    t.text((96, 484), "app testing while you keep working.", font=body, fill=MUTED, anchor="ls")
    label = font("seguisb.ttf", 22)
    t.text((seat[0] + 22, seat[1] + 24), "agent seat", font=label, fill=INK, anchor="lm")
    t.text((desk[0] + 22, desk[1] + 24), "your desktop", font=label, fill=zinc[300], anchor="lm")
    return image.convert("RGB")


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent
    out.mkdir(parents=True, exist_ok=True)
    render_mark(512).save(out / "anode-512.png", optimize=True)
    (out / "anode.ico").write_bytes(ico_bytes([render_mark(n) for n in ICON_SIZES]))
    social_preview().save(out / "social-preview.png", optimize=True)
    for name in ("anode-512.png", "anode.ico", "social-preview.png"):
        print(f"{out / name}  {(out / name).stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
