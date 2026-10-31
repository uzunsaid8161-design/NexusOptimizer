"""Erzeugt App-Icon (ICO/PNG) und ein Promo-Bild im NexusOptimizer-Look."""
import os, math
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(OUT, "src", "NexusOptimizer", "Assets")
os.makedirs(ASSETS, exist_ok=True)

CYAN = (34, 211, 238)
VIOLET = (139, 92, 246)
BG1 = (11, 14, 20)
BG2 = (19, 24, 41)


def lerp(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))


def gradient(size, c1, c2, angle=45):
    w, h = size
    y, x = np.mgrid[0:h, 0:w]
    rad = math.radians(angle)
    t = x * math.cos(rad) + y * math.sin(rad)
    t = (t - t.min()) / max(1e-6, (t.max() - t.min()))
    arr = np.zeros((h, w, 4), dtype=np.uint8)
    for i in range(3):
        arr[..., i] = (c1[i] + (c2[i] - c1[i]) * t).astype(np.uint8)
    arr[..., 3] = 255
    return Image.fromarray(arr, "RGBA")


def rounded_mask(size, radius):
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return mask


def hexagon(draw, cx, cy, r, fill):
    pts = [(cx + r * math.cos(math.radians(60 * i - 30)), cy + r * math.sin(math.radians(60 * i - 30))) for i in range(6)]
    draw.polygon(pts, fill=fill)


def make_logo(size=1024):
    # Grundfläche: Gradient
    base = gradient((size, size), (13, 20, 34), (38, 22, 70), 45)
    mask = rounded_mask((size, size), int(size * 0.24))

    # Glow-Blobs
    blob = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    bd = ImageDraw.Draw(blob)
    bd.ellipse([-size * 0.35, -size * 0.45, size * 0.75, size * 0.55], fill=CYAN + (70,))
    bd.ellipse([size * 0.35, size * 0.25, size * 1.25, size * 1.15], fill=VIOLET + (90,))
    blob = blob.filter(ImageFilter.GaussianBlur(size * 0.09))

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    img.alpha_composite(base)
    img.alpha_composite(blob)
    img.putalpha(Image.composite(img.getchannel("A"), Image.new("L", (size, size), 0), mask))

    d = ImageDraw.Draw(img)

    # Hexagon-Ring (HUD-Element)
    cx = cy = size / 2
    hexagon(d, cx, cy, size * 0.40, None)
    d.line([(cx + size * 0.40 * math.cos(math.radians(60 * i - 30)),
             cy + size * 0.40 * math.sin(math.radians(60 * i - 30))) for i in range(7)],
           fill=(255, 255, 255, 26), width=int(size * 0.012), joint="curve")

    # "N"-Monogramm
    font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", int(size * 0.56))
    text = "N"
    bbox = d.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx, ty = cx - tw / 2 - bbox[0], cy - th / 2 - bbox[1] - size * 0.01

    # Glow hinter dem Buchstaben
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(glow).text((tx, ty), text, font=font, fill=CYAN + (200,))
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.035))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)
    d.text((tx, ty), text, font=font, fill=(240, 248, 255, 255))

    # Akzent-Schnitt im N-Balken
    d.line([(cx - size * 0.045, cy - size * 0.10), (cx + size * 0.055, cy + size * 0.02)],
           fill=CYAN + (255,), width=int(size * 0.02))

    # Puls-Punkt oben rechts
    dot = (size * 0.735, size * 0.255)
    d.ellipse([dot[0] - size * 0.032, dot[1] - size * 0.032, dot[0] + size * 0.032, dot[1] + size * 0.032], fill=CYAN + (255,))

    img = img.filter(ImageFilter.UnsharpMask(radius=2, percent=60))
    img.putalpha(Image.composite(img.getchannel("A"), Image.new("L", (size, size), 0), mask))
    return img


def make_promo(logo, path):
    W, H = 1920, 1080
    img = gradient((W, H), BG1, BG2, 35).convert("RGBA")

    # Grid
    grid = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    for x in range(0, W, 64):
        gd.line([(x, 0), (x, H)], fill=(255, 255, 255, 10))
    for y in range(0, H, 64):
        gd.line([(0, y), (W, y)], fill=(255, 255, 255, 10))
    img = Image.alpha_composite(img, grid)

    # Glows
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-300, -420, 900, 620], fill=CYAN + (60,))
    gd.ellipse([1150, 520, 2300, 1500], fill=VIOLET + (80,))
    glow = glow.filter(ImageFilter.GaussianBlur(180))
    img = Image.alpha_composite(img, glow)

    # Logo
    lw = 380
    lg = logo.resize((lw, lw), Image.LANCZOS)
    img.alpha_composite(lg, (150, 330))

    d = ImageDraw.Draw(img)
    title_font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 118)
    sub_font = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 44)
    tag_font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 30)

    x = 600
    d.text((x, 380), "Nexus", font=title_font, fill=(240, 248, 255))
    d.text((x + 330, 380), "Optimizer", font=title_font, fill=CYAN)
    d.text((x + 4, 530), "Live-FPS · System-Telemetrie · Game-Tweaks", font=sub_font, fill=(180, 196, 220))

    # Tagline-Chips
    chips = ["60-s-Verlauf", "1 %-Lows", "Overlay", "SQLite-Sessions", "Dark Glass UI"]
    cx = x + 4
    for chip in chips:
        bbox = d.textbbox((0, 0), chip, font=tag_font)
        w = bbox[2] - bbox[0] + 48
        d.rounded_rectangle([cx, 640, cx + w, 700], radius=18, fill=(255, 255, 255, 18), outline=(255, 255, 255, 40))
        d.text((cx + 24, 654), chip, font=tag_font, fill=(214, 226, 245))
        cx += w + 18

    d.text((x + 4, 800), "eine .exe · keine Installation · Deutsch", font=tag_font,
           fill=(130, 144, 170))

    img.convert("RGB").save(path, quality=95)


logo = make_logo(1024)
logo.save(os.path.join(ASSETS, "app.png"))
logo.save(os.path.join(OUT, "brand-logo.png"))
make_promo(logo, os.path.join(OUT, "promo.png"))

ico = os.path.join(ASSETS, "app.ico")
logo.save(ico, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("assets:", os.listdir(ASSETS))
print("out:", [f for f in os.listdir(OUT) if f.endswith(".png")])
