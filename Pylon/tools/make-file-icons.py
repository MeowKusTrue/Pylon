"""Explorer icons for every format Pylon registers: Assets/FileIcons/<ext>.ico (16-256 px).

A folded page with the category glyph (play = video, note = audio) in the category colour and, from 40 px up, a pill
with the format name. Small sizes drop the pill: at 16-32 px it would only be noise.
Usage: python tools/make-file-icons.py      (needs Pillow; reads the extension lists from MainWindow.xaml.cs)
"""
import os, re
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "FileIcons")
FONT = r"C:\Windows\Fonts\segoeuib.ttf"
COLORS = {"video": ((0xF2, 0x9B, 0x3E), (0xC4, 0x66, 0x14)), "audio": ((0xE8, 0x5A, 0x95), (0xA9, 0x2F, 0x67))}
SIZES = [256, 128, 64, 48, 40, 32, 24, 20, 16]


def extensions(name):
    src = open(os.path.join(ROOT, "MainWindow.xaml.cs"), encoding="utf-8").read()
    body = re.search(name + r" = new\(StringComparer\.OrdinalIgnoreCase\)\s*\{([^}]*)\}", src).group(1)
    return re.findall(r'"\.([a-z0-9]+)"', body)


def gradient(size, box, top, bottom):
    """Vertical-ish diagonal gradient image covering box (x0, y0, x1, y1)."""
    g = Image.new("RGBA", (size, size))
    x0, y0, x1, y1 = box
    px = g.load()
    for y in range(max(0, int(y0)), min(size, int(y1) + 1)):
        for x in range(max(0, int(x0)), min(size, int(x1) + 1)):
            t = min(1, max(0, ((x - x0) / max(1, x1 - x0) * 0.35 + (y - y0) / max(1, y1 - y0) * 0.65)))
            px[x, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    return g


def page(d, k):
    """Folded page outline (256 grid scaled by k): rounded rect with the top-right corner cut."""
    r, x0, y0, x1, y1, fold = 18 * k, 44 * k, 14 * k, 212 * k, 242 * k, 56 * k
    d.rounded_rectangle((x0, y0, x1, y1), radius=r, fill=255)
    d.polygon([(x1 - fold, y0), (x1 + 1, y0), (x1 + 1, y0 + fold)], fill=0)  # cut corner


def icon(ext, kind, size):
    S = size * 4
    k = S / 256
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    mask = Image.new("L", (S, S), 0)
    page(ImageDraw.Draw(mask), k)
    img.paste(gradient(S, (0, 0, S, S), (0x3A, 0x3A, 0x3A), (0x22, 0x22, 0x22)), (0, 0), mask)
    fold = Image.new("L", (S, S), 0)
    ImageDraw.Draw(fold).polygon([(156 * k, 14 * k), (212 * k, 70 * k), (170 * k, 70 * k), (156 * k, 56 * k)], fill=255)
    img.paste(Image.new("RGBA", (S, S), (0x55, 0x55, 0x55, 255)), (0, 0), fold)

    labelled = size >= 40
    top, bottom = COLORS[kind]
    cy = 112 if labelled else 136  # glyph centre moves up to leave room for the pill
    scale = 0.82 if labelled else 1.0
    glyph = Image.new("L", (S, S), 0)
    g = ImageDraw.Draw(glyph)

    def P(x, y):
        return ((128 + (x - 128) * scale) * k, (cy + (y - 136) * scale) * k)

    if kind == "video":  # play triangle, corners softened by a round-joined outline
        tri = [P(102, 96), P(176, 136), P(102, 176)]
        g.polygon(tri, fill=255)
        g.line(tri + [tri[0]], fill=255, width=int(14 * k * scale), joint="curve")
    else:  # eighth note
        x, y = P(112, 162)
        g.ellipse((x - 26 * k * scale, y - 20 * k * scale, x + 26 * k * scale, y + 20 * k * scale), fill=255)
        g.rectangle((*P(126, 86), *P(140, 160)), fill=255)
        g.polygon([P(126, 84), P(172, 104), P(172, 126), P(140, 112)], fill=255)
    gx = [P(90, 80)[0], P(190, 190)[0]]
    img.paste(gradient(S, (gx[0], P(90, 80)[1], gx[1], P(190, 190)[1]), top, bottom), (0, 0), glyph)

    if labelled:
        text = ext.upper()
        font_size = {3: 40, 4: 34}.get(len(text), 28) * k
        font = ImageFont.truetype(FONT, int(font_size))
        w = ImageDraw.Draw(img).textlength(text, font=font)
        pw = max(w + 28 * k, 96 * k)
        x0, y0, x1, y1 = 128 * k - pw / 2, 172 * k, 128 * k + pw / 2, 222 * k
        pill = Image.new("L", (S, S), 0)
        ImageDraw.Draw(pill).rounded_rectangle((x0, y0, x1, y1), radius=12 * k, fill=255)
        img.paste(gradient(S, (x0, y0, x1, y1), top, bottom), (0, 0), pill)
        ImageDraw.Draw(img).text((128 * k, 197 * k), text, font=font, fill=(255, 255, 255, 255), anchor="mm")
    return img.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(OUT, exist_ok=True)
    made = []
    for kind, name in (("video", "VideoExt"), ("audio", "AudioExt")):
        for ext in extensions(name):
            frames = [icon(ext, kind, s) for s in SIZES]
            frames[0].save(os.path.join(OUT, ext + ".ico"), sizes=[(s, s) for s in SIZES], append_images=frames[1:])
            made.append(ext)
    print(len(made), "icons:", " ".join(made))


if __name__ == "__main__":
    main()
