"""Baut aus den aufgenommenen Frames ein optimiertes Demo-GIF."""
import os, glob, sys
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FRAMES = os.path.join(ROOT, "demo-frames")
OUT = os.path.join(ROOT, "docs", "img", "demo.gif")
POSTER = os.path.join(ROOT, "docs", "img", "demo-poster.png")

target_w = int(sys.argv[1]) if len(sys.argv) > 1 else 760
colors = int(sys.argv[2]) if len(sys.argv) > 2 else 128
step = int(sys.argv[3]) if len(sys.argv) > 3 else 1

files = sorted(glob.glob(os.path.join(FRAMES, "*.jpg")))[::step]
if len(sys.argv) > 4:
    files = files[: int(sys.argv[4])]
if not files:
    raise SystemExit("keine Frames gefunden")

fps = max(6, int(12 / step))
frames = []
for f in files:
    im = Image.open(f).convert("RGB")
    w, h = im.size
    im = im.resize((target_w, int(h * target_w / w)), Image.LANCZOS)
    frames.append(im)

# Erster Frame etwas halten, letzter Frame laenger stehen lassen
hold_start = [frames[0]] * int(fps * 0.4)
hold_end = [frames[-1]] * int(fps * 1.2)
seq = hold_start + frames + hold_end

pal = [f.quantize(colors=colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG) for f in seq]

pal[0].save(
    OUT,
    save_all=True,
    append_images=pal[1:],
    duration=int(1000 / fps),
    loop=0,
    optimize=True,
    disposal=2,
)
seq[len(seq) // 3].save(POSTER)

size_mb = os.path.getsize(OUT) / (1024 * 1024)
print(f"frames={len(seq)} width={target_w} colors={colors} fps={fps} size={size_mb:.1f} MB")
