"""Generate logy's Windows icon (app.ico) and a preview sheet.

Usage: python tools/make_icon.py
Writes src/logy/app.ico (default variant) and design/icon-*.png.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
SS = 4  # supersampling factor

VARIANTS = {
    # name: (tile colour, graph colour)
    "dark": ((0x3A, 0x3A, 0x3A), (0xFF, 0xFF, 0xFF)),
    "white": ((0xFF, 0xFF, 0xFF), (0x20, 0x20, 0x20)),
    "blue": ((0x00, 0x78, 0xD4), (0xFF, 0xFF, 0xFF)),
}


def _bezier(p0, p1, p2, p3, n=48):
    pts = []
    for i in range(n + 1):
        t = i / n
        a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t ** 2, t ** 3
        pts.append((a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0],
                    a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]))
    return pts


def render(size, tile, fg, bold=False):
    """Draw the icon on a 1024 grid, supersampled, then downscale to `size`."""
    big = 1024 * SS
    s = SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 40 * s
    d.rounded_rectangle((pad, pad, big - pad, big - pad), radius=200 * s, fill=tile)

    stroke = (112 if bold else 64) * s
    r = (124 if bold else 80) * s
    xm, xb = (350 if bold else 360) * s, (690 if bold else 664) * s
    top, bot, mid = 240 * s, 784 * s, 512 * s

    def line(pts):
        d.line(pts, fill=fg, width=stroke, joint="curve")
        for x, y in (pts[0], pts[-1]):
            d.ellipse((x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2), fill=fg)

    # main lane
    line([(xm, top), (xm, bot)])
    # branch: leaves main, runs straight down its own lane, merges back
    out = _bezier((xm, top + 40 * s), (xm, 380 * s), (xb, 300 * s), (xb, 440 * s))
    back = _bezier((xb, 584 * s), (xb, 724 * s), (xm, 644 * s), (xm, bot - 40 * s))
    line(out + [(xb, 584 * s)] + back[1:])
    # commits: solid dots
    for x, y in ((xm, top), (xb, mid), (xm, bot)):
        d.ellipse((x - r, y - r, x + r, y + r), fill=fg)

    return img.resize((size, size), Image.LANCZOS)


def build_ico(variant, out):
    tile, fg = VARIANTS[variant]
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    imgs = [render(sz, tile, fg, bold=sz <= 32) for sz in sizes]
    out.parent.mkdir(parents=True, exist_ok=True)
    imgs[0].save(out, format="ICO", sizes=[(i.width, i.height) for i in imgs],
                 append_images=imgs[1:])


def preview(out):
    """One row per variant: 256, 64, 48, 32, 24, 16 px, then 16 px blown up 6x."""
    show = [256, 64, 48, 32, 24, 16]
    row_h, gap = 290, 28
    w = sum(show) + gap * (len(show) + 1) + 96 + gap + 140
    h = row_h * len(VARIANTS) + 40
    sheet = Image.new("RGB", (w, h), (0x20, 0x20, 0x20))
    d = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("segoeui.ttf", 18)
    except OSError:
        font = ImageFont.load_default()
    y = 20
    for name, (tile, fg) in VARIANTS.items():
        d.text((gap, y + row_h // 2 - 12), name, fill=(0xAD, 0xAD, 0xAD), font=font)
        x = 140
        for sz in show:
            ic = render(sz, tile, fg, bold=sz <= 32)
            sheet.paste(ic, (x, y + (256 - sz) // 2), ic)
            d.text((x, y + 262), f"{sz}", fill=(0x8A, 0x8A, 0x8A), font=font)
            x += sz + gap
        zoom = render(16, tile, fg, bold=True).resize((96, 96), Image.NEAREST)
        sheet.paste(zoom, (x, y + 80), zoom)
        d.text((x, y + 262), "16 x6", fill=(0x8A, 0x8A, 0x8A), font=font)
        y += row_h
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)


if __name__ == "__main__":
    build_ico("white", ROOT / "src" / "logy" / "app.ico")
    for v in VARIANTS:
        build_ico(v, ROOT / "design" / f"app-{v}.ico")
        render(256, *VARIANTS[v]).save(ROOT / "design" / f"icon-{v}-256.png")
    preview(ROOT / "design" / "icon-preview.png")
    print("ok")
