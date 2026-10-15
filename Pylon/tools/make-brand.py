"""Pictures for the services Pylon talks to and for posts about it: docs/brand/*.png.

The mark comes straight from Assets/Icons/logo.svg (stem and leg light, play triangle orange, as in pylon.ico), drawn at
4x and scaled down for smooth edges.
  pylon.png           1024 x 1024  Discord Rich Presence large image "pylon" (and any app logo field)
  play.png, pause.png  512 x 512   Discord small images "play" / "pause"
  pylon-512.png        512 x 512   app avatar for Last.fm, AcoustID, OpenSubtitles if they ask
  social-preview.png  1280 x 640   GitHub social preview (repo Settings) and posts
Usage: python tools/make-brand.py      (needs Pillow)
"""
import os, re
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(os.path.dirname(ROOT), "docs", "brand")
BG, LIGHT = (0x1B, 0x1B, 0x1B), (0xED, 0xED, 0xED)
ORANGE = ((0xE9, 0x90, 0x36), (0xBF, 0x69, 0x1B))  # top, bottom of the triangle's gradient
FONT = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_TEXT = r"C:\Windows\Fonts\segoeui.ttf"


def logo_paths():
    """[(fill, [points])] from logo.svg: M/L/C/Z in absolute 256-unit coordinates, curves flattened."""
    svg = open(os.path.join(ROOT, "Assets", "Icons", "logo.svg"), encoding="utf-8").read()
    dx = float(re.search(r'translate\((-?[\d.]+)', svg).group(1)) if "translate(" in svg else 0
    shapes = []
    for fill, d in re.findall(r'<path fill="(\w+)" d="([^"]+)"', svg):
        nums = iter(float(n) for n in re.findall(r"-?[\d.]+", d))
        pts, cmd = [], None
        for token in re.findall(r"[MLCZ]|-?[\d.]+", d):
            if token in "MLCZ":
                cmd = token
                if cmd == "M" or cmd == "L":
                    pts.append((next(nums) + dx, next(nums)))
                elif cmd == "C":
                    (x0, y0), c = pts[-1], [(next(nums) + dx, next(nums)) for _ in range(3)]
                    for i in range(1, 25):
                        t = i / 24
                        u = 1 - t
                        pts.append((u ** 3 * x0 + 3 * u * u * t * c[0][0] + 3 * u * t * t * c[1][0] + t ** 3 * c[2][0],
                                    u ** 3 * y0 + 3 * u * u * t * c[0][1] + 3 * u * t * t * c[1][1] + t ** 3 * c[2][1]))
        shapes.append((fill, pts))
    return shapes


def gradient(size, box, top, bottom):
    w, h = size
    g = Image.new("RGBA", (w, h))
    x0, y0, x1, y1 = box
    px = g.load()
    for y in range(h):
        t = min(1, max(0, (y - y0) / max(1, y1 - y0)))
        row = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        for x in range(w):
            px[x, y] = row
    return g


def draw_mark(img, x, y, size):
    """The logo, its 256-unit box placed at (x, y) and scaled to size."""
    k = size / 256
    for fill, pts in logo_paths():
        mask = Image.new("L", img.size, 0)
        ImageDraw.Draw(mask).polygon([(x + px * k, y + py * k) for px, py in pts], fill=255)
        if fill == "accentColor":
            ys = [y + py * k for _, py in pts]
            img.paste(gradient(img.size, (0, min(ys), 0, max(ys)), *ORANGE), (0, 0), mask)
        else:
            img.paste(Image.new("RGBA", img.size, LIGHT + (255,)), (0, 0), mask)


def app_icon(size):
    S = size * 4
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle((0, 0, S - 1, S - 1), radius=S * 0.22, fill=BG + (255,))
    draw_mark(img, S * 0.1, S * 0.1, S * 0.8)
    return img.resize((size, size), Image.LANCZOS)


def badge(kind, size):
    """Discord's small image: an orange disc with a light play / pause glyph (shown about 20 px wide)."""
    S = size * 4
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    disc = Image.new("L", (S, S), 0)
    ImageDraw.Draw(disc).ellipse((0, 0, S - 1, S - 1), fill=255)
    img.paste(gradient((S, S), (0, 0, 0, S), *ORANGE), (0, 0), disc)
    d = ImageDraw.Draw(img)
    if kind == "play":
        tri = [(S * 0.38, S * 0.27), (S * 0.76, S * 0.5), (S * 0.38, S * 0.73)]
        r = S * 0.03  # soft corners: a round dot at each vertex plus the edges drawn that thick
        d.polygon(tri, fill=LIGHT)
        for i, (x, y) in enumerate(tri):
            d.ellipse((x - r, y - r, x + r, y + r), fill=LIGHT)
            d.line([tri[i], tri[(i + 1) % 3]], fill=LIGHT, width=int(2 * r))
    else:
        for x in (0.33, 0.56):
            d.rounded_rectangle((S * x, S * 0.28, S * (x + 0.11), S * 0.72), radius=S * 0.03, fill=LIGHT)
    return img.resize((size, size), Image.LANCZOS)


def social(w=1280, h=640):
    S = 2
    img = Image.new("RGBA", (w * S, h * S), BG + (255,))
    mark = 300 * S
    draw_mark(img, 150 * S, (h * S - mark) // 2, mark)
    d = ImageDraw.Draw(img)
    d.text((500 * S, 250 * S), "Pylon", font=ImageFont.truetype(FONT, 120 * S), fill=LIGHT, anchor="ls")
    sub = ImageFont.truetype(FONT_TEXT, 34 * S)
    d.text((506 * S, 330 * S), "A media player for Windows, built on libVLC", font=sub, fill=(0xA0, 0xA0, 0xA0), anchor="ls")
    d.text((506 * S, 382 * S), "Synced lyrics · effects · streaming · open source", font=sub, fill=(0x80, 0x80, 0x80), anchor="ls")
    return img.resize((w, h), Image.LANCZOS)


def main():
    os.makedirs(OUT, exist_ok=True)
    app_icon(1024).save(os.path.join(OUT, "pylon.png"))
    app_icon(512).save(os.path.join(OUT, "pylon-512.png"))
    badge("play", 512).save(os.path.join(OUT, "play.png"))
    badge("pause", 512).save(os.path.join(OUT, "pause.png"))
    social().convert("RGB").save(os.path.join(OUT, "social-preview.png"))
    print("written to", OUT, sorted(os.listdir(OUT)))


if __name__ == "__main__":
    main()
