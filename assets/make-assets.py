"""Regenerate Anode's raster brand assets.

Usage: python assets/make-assets.py [output-directory] [--proof]

Needs Python 3 and Pillow. Reads anode.svg and Segoe UI from the Windows Fonts folder;
no network. Writes anode-512.png, anode.ico and social-preview.png next to this script
unless an output directory is given. --proof adds a light/dark icon-size proof sheet.

The SVG is the single source for the frame, screen, pointer and brand colors. It uses
only rounded rectangles and a polygon so exports do not need a browser or SVG library.
Every size is drawn at 8x and box-filtered without ringing. Small frames snap to whole
pixels; the pointer retains the same proportions at every size.
"""

import argparse
import io
import os
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageColor, ImageDraw, ImageFont

MASTER = ET.parse(Path(__file__).with_name("anode.svg")).getroot()
if MASTER.get("viewBox") != "0 0 256 256":
    raise ValueError("The master mark must use a 256 x 256 viewBox.")


def shape(name, tag):
    element = MASTER.find(f"{{http://www.w3.org/2000/svg}}{tag}[@id='{name}']")
    if element is None:
        raise ValueError(f"Missing {tag} #{name} in anode.svg")
    return element


def rectangle(element):
    x, y, w, h, r = (float(element.attrib[key]) for key in ("x", "y", "width", "height", "rx"))
    return (x, y, x + w, y + h), r


FRAME = shape("frame", "rect")
SCREEN = shape("screen", "rect")
CURSOR = shape("pointer", "polygon")
INK = ImageColor.getcolor(FRAME.attrib["fill"], "RGBA")
ACCENT = ImageColor.getcolor(SCREEN.attrib["fill"], "RGBA")
POINTER_INK = ImageColor.getcolor(CURSOR.attrib["fill"], "RGBA")
POINTER = [tuple(map(float, point.split(","))) for point in CURSOR.attrib["points"].split()]
POINTER_HEIGHT = max(y for _, y in POINTER) - POINTER[0][1]
MUTED = (161, 161, 170, 255)
PAPER = (250, 250, 250, 255)

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SUPERSAMPLE = 8
FONTS = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"


def pointer_at(tip, height):
    k = height / POINTER_HEIGHT
    ox, oy = POINTER[0]
    return [(tip[0] + (x - ox) * k, tip[1] + (y - oy) * k) for x, y in POINTER]


def mark_geometry(size):
    """Fit the SVG to a square, with whole-pixel screen edges at tray sizes."""
    k = size / 256
    fit = (lambda v: round(v * k)) if size <= 48 else (lambda v: v * k)
    outer, radius = rectangle(FRAME)
    inner, inner_radius = rectangle(SCREEN)
    frame = tuple(fit(v) for v in outer)
    if size <= 48:
        # Rounding both rectangles independently can erase a border (e.g. at 16 px).
        insets = [max(1, round(abs(a - b) * k)) for a, b in zip(outer, inner)]
        screen = tuple(v + inset * sign for v, inset, sign in zip(frame, insets, (1, 1, -1, -1)))
        inner_radius = max(0, radius * k - min(insets))
    else:
        screen = tuple(v * k for v in inner)
        inner_radius *= k
    boxes = [(frame, radius * k), (screen, inner_radius)]
    tip = tuple(fit(v) for v in POINTER[0])
    return boxes, pointer_at(tip, POINTER_HEIGHT * k)


def fill_box(draw, box, radius, fill, s):
    x0, y0, x1, y1 = box
    draw.rounded_rectangle((x0 * s, y0 * s, x1 * s - 1, y1 * s - 1), radius=radius * s, fill=fill)


def fill_polygon(draw, points, fill, s):
    # Pillow fills every pixel a polygon touches, so it grows one supersampled pixel to the right
    # and bottom; the pointer's long left edge and tip stay exact.
    draw.polygon([(x * s, y * s) for x, y in points], fill=fill)


def draw_mark(draw, size, s, offset=(0, 0)):
    """Draw the mark for a `size` px icon onto `draw`, which is `s` times larger than the target."""
    boxes, pointer = mark_geometry(size)
    dx, dy = offset
    for ((x0, y0, x1, y1), radius), color in zip(boxes, (INK, ACCENT)):
        fill_box(draw, (x0 + dx, y0 + dy, x1 + dx, y1 + dy), radius, color, s)
    fill_polygon(draw, [(x + dx, y + dy) for x, y in pointer], POINTER_INK, s)


def render_mark(size):
    s = SUPERSAMPLE
    image = Image.new("RGBA", (size * s, size * s), (0, 0, 0, 0))
    draw_mark(ImageDraw.Draw(image), size, s)
    return image.reduce(s)


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


def proof_sheet():
    """Actual-size icons above enlarged pixels, on both taskbar backgrounds."""
    proof = Image.new("RGB", (960, 520), PAPER)
    d = ImageDraw.Draw(proof)
    sizes = (16, 20, 24, 32, 40, 48, 64, 128)
    for row, background in enumerate((PAPER, INK)):
        y = row * 260
        d.rectangle((0, y, 960, y + 259), fill=background)
        foreground = INK if row == 0 else PAPER
        for i, size in enumerate(sizes):
            x = i * 120 + 60
            mark = render_mark(size)
            proof.paste(mark, (x - size // 2, y + 32), mark)
            if size <= 32:
                zoom = mark.resize((size * 3, size * 3), Image.Resampling.NEAREST)
                proof.paste(zoom, (x - zoom.width // 2, y + 112), zoom)
            d.text((x, y + 238), f"{size} px", font=font("segoeui.ttf", 14), fill=foreground, anchor="mm")
    return proof


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_directory", nargs="?", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--proof", action="store_true", help="also export a light/dark icon-size proof sheet")
    args = parser.parse_args()
    out = args.output_directory
    out.mkdir(parents=True, exist_ok=True)
    render_mark(512).save(out / "anode-512.png", optimize=True)
    (out / "anode.ico").write_bytes(ico_bytes([render_mark(n) for n in ICON_SIZES]))
    social_preview().save(out / "social-preview.png", optimize=True)
    names = ["anode-512.png", "anode.ico", "social-preview.png"]
    if args.proof:
        proof_sheet().save(out / "icon-proof.png")
        names.append("icon-proof.png")
    for name in names:
        print(f"{out / name}  {(out / name).stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
