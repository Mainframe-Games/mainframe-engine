#!/usr/bin/env python3
"""Draws Neon's posters: Examples/Neon/Assets/Art/Apartment/Textures/Poster_<n>.png (512 x 768, sRGB).

    python3 Examples/Neon/Tools/apartment/make_posters.py

Original art made here from shapes and the engine's own fonts (Lato, OFL; Roboto Mono, Apache 2.0): fictional titles,
and "glyph columns" that look like signage but are random strokes, not text in any language. Seeded, so every run
writes the same images. The apartment's script (build_apartment.py) hangs them in frames.
"""

import math
import os
import random

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", "..", ".."))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Art", "Apartment", "Textures"))
FONTS = os.path.join(ROOT, "MainframeEngine", "Assets", "UI", "fonts")
W, H = 512, 768


def font(name, size):
    return ImageFont.truetype(os.path.join(FONTS, name), size)


BOLD = "LatoLatin-Bold.ttf"
MONO = "RobotoMono-Regular.ttf"


def gradient(top, bottom):
    img = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(img)
    for y in range(H):
        t = y / (H - 1)
        d.line([(0, y), (W, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)))
    return img


def glyph(d, x, y, s, rng, fill, width=3):
    """A made-up glyph in an s x s cell: 3-6 strokes on a grid (not a character of any script)."""
    pts = [(x + s * gx / 4, y + s * gy / 4) for gx in range(5) for gy in range(5)]
    for _ in range(rng.randint(3, 6)):
        a = rng.choice(pts)
        horizontal = rng.random() < 0.5
        b = (a[0] + s * rng.choice((0.5, 0.75, 1.0)) * (1 if a[0] < x + s / 2 else -1), a[1]) if horizontal else \
            (a[0], a[1] + s * rng.choice((0.25, 0.5, 0.75)))
        d.line([a, b], fill=fill, width=width)


def glyph_column(d, x, y, count, s, rng, fill, width=3):
    for i in range(count):
        glyph(d, x, y + i * s * 1.25, s, rng, fill, width)


def title(d, text, y, size, fill, name=BOLD, x=None, spacing=0):
    f = font(name, size)
    if spacing:
        widths = [d.textlength(c, font=f) for c in text]
        total = sum(widths) + spacing * (len(text) - 1)
        cx = (W - total) / 2 if x is None else x
        for c, w in zip(text, widths):
            d.text((cx, y), c, font=f, fill=fill)
            cx += w + spacing
        return
    w = d.textlength(text, font=f)
    d.text(((W - w) / 2 if x is None else x, y), text, font=f, fill=fill)


def neo_kaiju(rng):
    img = gradient((20, 6, 18), (70, 10, 24))
    d = ImageDraw.Draw(img)
    d.ellipse([86, 120, 426, 460], fill=(230, 40, 60))
    # A mech silhouette from polygons.
    body = [(180, 640), (200, 470), (170, 400), (220, 330), (300, 310), (340, 360), (330, 420), (360, 470), (350, 640)]
    d.polygon(body, fill=(12, 8, 14))
    d.polygon([(220, 330), (250, 250), (290, 240), (310, 300)], fill=(12, 8, 14))
    d.polygon([(330, 420), (430, 380), (440, 400), (345, 450)], fill=(12, 8, 14))
    d.ellipse([268, 262, 282, 276], fill=(255, 230, 120))
    glyph_column(d, 30, 60, 8, 34, rng, (255, 220, 225))
    title(d, "NEO KAIJU", 662, 64, (255, 240, 240))
    title(d, "TOWER 9 · LIVE · 23:00", 732, 18, (255, 120, 140), MONO)
    return img


def volt_seven(rng):
    img = gradient((10, 4, 30), (60, 10, 70))
    d = ImageDraw.Draw(img)
    for i in range(9):  # a striped sun
        y0 = 180 + i * 26
        d.rectangle([126, y0, 386, y0 + 16 - i], fill=(255, 80 + i * 18, 160 - i * 10))
    d.ellipse([126, 150, 386, 410], outline=None)
    horizon = 420
    for i in range(14):  # the grid floor
        y = horizon + (i ** 1.7) * 2.4
        d.line([(0, y), (W, y)], fill=(255, 40, 200), width=2)
    for i in range(-12, 13):
        d.line([(W / 2 + i * 8, horizon), (W / 2 + i * 70, H)], fill=(255, 40, 200), width=2)
    title(d, "VOLT", 40, 120, (120, 255, 250))
    title(d, "SEVEN", 172, 30, (255, 255, 255), MONO, spacing=18)
    return img


def sakura_null(rng):
    img = Image.new("RGB", (W, H), (8, 8, 12))
    d = ImageDraw.Draw(img)
    for _ in range(90):
        r = rng.uniform(4, 26)
        x, y = rng.uniform(0, W), rng.uniform(0, H * 0.8)
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, int(rng.uniform(120, 190)), int(rng.uniform(170, 220))))
    for _ in range(14):  # glitch bars
        y = rng.uniform(0, H)
        h = rng.uniform(2, 12)
        shift = rng.uniform(-40, 40)
        band = img.crop((0, int(y), W, int(y + h)))
        img.paste(band, (int(shift), int(y)))
        d.rectangle([0, y, W, y + 1], fill=(0, 255, 230))
    title(d, "SAKURA//NULL", 630, 52, (255, 255, 255))
    title(d, "season 0 · no signal", 700, 22, (0, 255, 230), MONO)
    return img


def oni_protocol(rng):
    img = Image.new("RGB", (W, H), (180, 14, 24))
    d = ImageDraw.Draw(img)
    cx, cy = W / 2, 330
    d.polygon([(cx - 170, cy - 140), (cx + 170, cy - 140), (cx + 120, cy + 170), (cx - 120, cy + 170)], fill=(14, 10, 12))
    d.polygon([(cx - 170, cy - 140), (cx - 210, cy - 260), (cx - 110, cy - 150)], fill=(14, 10, 12))
    d.polygon([(cx + 170, cy - 140), (cx + 210, cy - 260), (cx + 110, cy - 150)], fill=(14, 10, 12))
    for sx in (-1, 1):
        d.polygon([(cx + sx * 40, cy - 40), (cx + sx * 130, cy - 70), (cx + sx * 110, cy - 10)], fill=(255, 220, 60))
    for i in range(7):
        x = cx - 90 + i * 30
        d.polygon([(x, cy + 90), (x + 15, cy + 130), (x + 30, cy + 90)], fill=(240, 236, 220))
    title(d, "ONI", 560, 110, (14, 10, 12))
    title(d, "PROTOCOL", 680, 34, (14, 10, 12), MONO, spacing=10)
    return img


def drift_city(rng):
    img = gradient((0, 40, 60), (4, 8, 20))
    d = ImageDraw.Draw(img)
    x = 0
    while x < W:
        w = rng.uniform(30, 70)
        h = rng.uniform(160, 460)
        d.rectangle([x, H - 160 - h, x + w, H - 160], fill=(6, 10, 18))
        for wy in range(int(H - 150 - h), H - 170, 14):
            for wx in range(int(x + 6), int(x + w - 6), 10):
                if rng.random() < 0.35:
                    d.rectangle([wx, wy, wx + 4, wy + 6], fill=rng.choice([(0, 255, 220), (255, 60, 190), (255, 200, 90)]))
        x += w + rng.uniform(0, 8)
    d.rectangle([0, H - 160, W, H], fill=(4, 6, 12))
    title(d, "DRIFT", 640, 70, (0, 255, 220))
    title(d, "CITY", 712, 34, (255, 255, 255), MONO, spacing=22)
    glyph_column(d, 452, 40, 6, 30, rng, (0, 255, 220), 2)
    return img


def halo_nine(rng):
    img = Image.new("RGB", (W, H), (230, 226, 214))
    d = ImageDraw.Draw(img)
    cx, cy = W / 2, 320
    for i in range(12, 0, -1):
        r = i * 18
        d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(20, 20, 26), width=3 if i % 3 else 7)
    d.ellipse([cx - 40, cy - 40, cx + 40, cy + 40], fill=(255, 70, 30))
    title(d, "HALO-9", 600, 88, (20, 20, 26))
    title(d, "SUBORBITAL · 2077 · TICKETS AT ANY NODE", 712, 15, (20, 20, 26), MONO)
    return img


def static_monks(rng):
    img = Image.new("RGB", (W, H), (20, 18, 30))
    d = ImageDraw.Draw(img)
    for gy in range(0, H, 12):  # a halftone head
        for gx in range(0, W, 12):
            dx, dy = (gx - W / 2) / 170, (gy - 330) / 220
            v = max(0.0, 1 - math.hypot(dx, dy))
            v *= 0.6 + 0.4 * math.sin(gx * 0.05 + gy * 0.03)
            r = 6 * v
            if r > 0.5:
                d.ellipse([gx - r, gy - r, gx + r, gy + r], fill=(255, 200, 40))
    title(d, "THE STATIC", 590, 56, (255, 255, 255))
    title(d, "MONKS", 650, 76, (255, 200, 40))
    title(d, "drone / noise / prayer", 736, 18, (180, 180, 200), MONO)
    return img


def koi_engine(rng):
    img = gradient((6, 14, 40), (2, 4, 14))
    d = ImageDraw.Draw(img)
    for k, (cx, cy, a) in enumerate(((180, 260, 0.6), (330, 430, 3.6))):
        for i in range(14):  # a koi from overlapping ellipses along a curve
            t = i / 13
            x = cx + math.cos(a) * 160 * (t - 0.5) + math.sin(t * 3 + k) * 18
            y = cy + math.sin(a) * 160 * (t - 0.5)
            r = 46 * (1 - t) ** 0.7 + 6
            col = (255, 120 + int(60 * t), 30) if (i + k) % 4 else (245, 245, 240)
            d.ellipse([x - r, y - r * 0.7, x + r, y + r * 0.7], fill=col)
    img = img.filter(ImageFilter.GaussianBlur(0.6))
    d = ImageDraw.Draw(img)
    title(d, "KOI ENGINE", 640, 60, (255, 140, 40))
    title(d, "flow state / 24h", 712, 22, (120, 180, 255), MONO)
    return img


POSTERS = [neo_kaiju, volt_seven, sakura_null, oni_protocol, drift_city, halo_nine, static_monks, koi_engine]


def main():
    os.makedirs(OUT, exist_ok=True)
    for i, draw in enumerate(POSTERS):
        img = draw(random.Random(77 + i))
        # Print texture: a faint paper grain and a darker edge.
        grain = random.Random(1000 + i)
        px = img.load()
        for _ in range(9000):
            x, y = grain.randrange(W), grain.randrange(H)
            r, g, b = px[x, y]
            k = grain.uniform(0.85, 1.0)
            px[x, y] = (int(r * k), int(g * k), int(b * k))
        img.save(os.path.join(OUT, f"Poster_{i + 1}.png"), "PNG", optimize=True)
        print("poster", i + 1, draw.__name__)


if __name__ == "__main__":
    main()
