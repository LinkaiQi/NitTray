#!/usr/bin/env python3
"""Generate NitTray's brand icons.

Draws a modern "display + brightness" mark — a monitor with a glowing brightness
sun on its screen (a nod to the *nit*, the unit of luminance) — and writes the
production assets consumed by the app:

    src/NitTray/Assets/app.ico          multi-size app / window / taskbar icon
    src/NitTray/Assets/tray-light.ico   white tray glyph  (for dark taskbars)
    src/NitTray/Assets/tray-dark.ico    dark tray glyph   (for light taskbars)

Everything is drawn vector-style, supersampled 4x, and downsampled with LANCZOS.
The .ico files embed a natively rendered (crisp) bitmap per size, PNG-compressed.

Usage:
    python3 -m pip install Pillow
    python3 tools/icons/generate_icons.py
"""
import io
import math
import os
import struct

from PIL import Image, ImageDraw

SS = 4  # supersample factor
ASSETS = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..",
    "src", "NitTray", "Assets"))


# ---- drawing helpers ------------------------------------------------------
def _rmask(size, radius, box=None):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(
        box or [0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def _capsule(draw, p0, p1, width, fill):
    r = width / 2
    draw.ellipse([p0[0] - r, p0[1] - r, p0[0] + r, p0[1] + r], fill=fill)
    draw.ellipse([p1[0] - r, p1[1] - r, p1[0] + r, p1[1] + r], fill=fill)
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    L = math.hypot(dx, dy) or 1
    nx, ny = -dy / L * r, dx / L * r
    draw.polygon([(p0[0] + nx, p0[1] + ny), (p1[0] + nx, p1[1] + ny),
                  (p1[0] - nx, p1[1] - ny), (p0[0] - nx, p0[1] - ny)], fill=fill)


def _sun(size, cx, cy, color, disc_r, ir, orr, rw, n=8):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i in range(n):
        th = i * (2 * math.pi / n)
        _capsule(d, (cx + ir * math.cos(th), cy + ir * math.sin(th)),
                 (cx + orr * math.cos(th), cy + orr * math.sin(th)), rw, color)
    d.ellipse([cx - disc_r, cy - disc_r, cx + disc_r, cy + disc_r], fill=color)
    return img


# ---- the marks ------------------------------------------------------------
def app_icon(size):
    s = size * SS
    tile = (30, 32, 50)      # flat mica-dark
    frame = (244, 246, 249)  # flat white monitor
    screen = (20, 22, 32)    # flat dark panel
    stand = (198, 203, 218)
    sun = (255, 200, 60)     # flat amber

    img = Image.new("RGBA", (s, s), tile + (255,))
    d = ImageDraw.Draw(img)

    sw, sh = 0.66 * s, 0.48 * s
    sx, sy = (s - sw) / 2, 0.185 * s
    scx, scy = s / 2, sy + sh / 2
    rad = 0.050 * s

    # Stand: trapezoid neck (wider at the bottom) + a wide rounded base.
    ny0, ny1 = sy + sh - 0.005 * s, sy + sh + 0.065 * s
    d.polygon([(scx - 0.038 * s, ny0), (scx + 0.038 * s, ny0),
               (scx + 0.058 * s, ny1), (scx - 0.058 * s, ny1)], fill=stand)
    d.rounded_rectangle([scx - 0.155 * s, ny1 - 0.004 * s, scx + 0.155 * s,
                         ny1 + 0.040 * s], radius=0.020 * s, fill=stand)

    # Monitor: thick white frame + flat dark screen.
    d.rounded_rectangle([sx, sy, sx + sw, sy + sh], radius=rad, fill=frame)
    bez = 0.042 * s  # thick bezel so the monitor border reads clearly
    isx, isy, isw, ish = sx + bez, sy + bez, sw - 2 * bez, sh - 2 * bez
    d.rounded_rectangle([isx, isy, isx + isw, isy + ish],
                        radius=max(1, rad - bez * 0.5), fill=screen)

    # Sun: large bold disc + short rays with a clear gap.
    img = Image.alpha_composite(img, _sun(s, scx, scy, sun + (255,),
                                          disc_r=0.088 * s, ir=0.128 * s,
                                          orr=0.170 * s, rw=0.036 * s))

    # Faint Fluent card edge (a thin line, not a gradient).
    stroke = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(stroke).rounded_rectangle(
        [1, 1, s - 2, s - 2], radius=0.225 * s, outline=(255, 255, 255, 36),
        width=max(1, int(0.005 * s)))
    img = Image.alpha_composite(img, stroke)

    img.putalpha(_rmask(s, 0.225 * s))
    return img.resize((size, size), Image.LANCZOS)


def tray_glyph(size, color):
    """A monitor outline with a small sun inside — the tray mark."""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    sw, sh = 0.82 * s, 0.58 * s
    sx, sy = (s - sw) / 2, 0.13 * s
    scx, scy = s / 2, sy + sh / 2
    stroke = int(0.090 * s)  # thicker outline
    d.rounded_rectangle([sx, sy, sx + sw, sy + sh], radius=0.12 * s,
                        outline=color, width=stroke)
    _capsule(d, (scx, sy + sh), (scx, sy + sh + 0.08 * s), stroke, color)
    _capsule(d, (scx - 0.14 * s, sy + sh + 0.10 * s),
             (scx + 0.14 * s, sy + sh + 0.10 * s), stroke, color)
    img = Image.alpha_composite(img, _sun(s, scx, scy, color, disc_r=0.086 * s,
                                          ir=0.124 * s, orr=0.162 * s, rw=0.034 * s))
    return img.resize((size, size), Image.LANCZOS)


# ---- .ico writer ----------------------------------------------------------
def write_ico(images, path):
    """Write a PNG-compressed multi-resolution .ico from per-size images."""
    images = sorted(images, key=lambda im: im.width)
    blobs = []
    for im in images:
        b = io.BytesIO()
        im.save(b, format="PNG")
        blobs.append(b.getvalue())
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for im, blob in zip(images, blobs):
        w = 0 if im.width >= 256 else im.width
        h = 0 if im.height >= 256 else im.height
        out.write(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    for blob in blobs:
        out.write(blob)
    with open(path, "wb") as f:
        f.write(out.getvalue())


def main():
    os.makedirs(ASSETS, exist_ok=True)
    write_ico([app_icon(s) for s in (16, 24, 32, 48, 64, 128, 256)],
              os.path.join(ASSETS, "app.ico"))
    write_ico([tray_glyph(s, (255, 255, 255)) for s in (16, 20, 24, 32)],
              os.path.join(ASSETS, "tray-light.ico"))
    write_ico([tray_glyph(s, (32, 32, 32)) for s in (16, 20, 24, 32)],
              os.path.join(ASSETS, "tray-dark.ico"))
    print("Wrote icons to", ASSETS)


if __name__ == "__main__":
    main()
