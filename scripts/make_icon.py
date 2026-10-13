#!/usr/bin/env python3
"""Draws the app icon and writes Resources/AppIcon.icns (no iconutil needed)."""
import io, math, struct
from PIL import Image, ImageDraw, ImageFilter

S = 2048  # supersampled canvas

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

# background: macOS icon grid (824/1024 body, ~22.5% corner radius)
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
body = int(S * 824 / 1024); off = (S - body) // 2
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    gd.line([(0, y), (S, y)], fill=lerp((34, 38, 58), (10, 12, 20), y / S) + (255,))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([off, off, off + body, off + body], radius=int(body * 0.225), fill=255)

# soft drop shadow
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
shadow.paste((0, 0, 0, 150), mask=mask)
shadow = shadow.filter(ImageFilter.GaussianBlur(S * 0.012))
img.alpha_composite(shadow, (0, int(S * 0.01)))
img.paste(grad, mask=mask)

# warm glow behind the book
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse([S*0.22, S*0.28, S*0.78, S*0.84], fill=(200, 169, 110, 70))
glow = glow.filter(ImageFilter.GaussianBlur(S * 0.07))
glow_masked = Image.new("RGBA", (S, S), (0, 0, 0, 0)); glow_masked.paste(glow, mask=mask)
img.alpha_composite(glow_masked)

d = ImageDraw.Draw(img)
GOLD = (200, 169, 110, 255); GOLD_L = (232, 201, 142, 255); GOLD_D = (138, 112, 64, 255)
cx = S // 2; top = int(S * 0.50); bot = int(S * 0.74)
half = int(S * 0.27)

def page(sign):
    pts = []
    n = 40
    # outer curved top edge (page bows upward toward the spine)
    for i in range(n + 1):
        t = i / n
        x = cx + sign * (half * t)
        y = top - math.sin(t * math.pi) * S * 0.035 + (t ** 2) * S * 0.04 * -1 + S * 0.0
        pts.append((x, y))
    # outer bottom edge
    for i in range(n, -1, -1):
        t = i / n
        x = cx + sign * (half * t)
        y = bot - math.sin(t * math.pi) * S * 0.012
        pts.append((x, y))
    return pts

for sign, col in ((-1, GOLD), (1, GOLD_L)):
    d.polygon(page(sign), fill=col)
# spine
d.rectangle([cx - int(S*0.004), top - int(S*0.01), cx + int(S*0.004), bot], fill=GOLD_D)
# text lines
for sign in (-1, 1):
    for k in range(4):
        y = top + int(S * 0.03) + k * int(S * 0.04)
        x0 = cx + sign * int(S * 0.04); x1 = cx + sign * int(S * (0.22 - k * 0.012))
        d.line([(x0, y), (x1, y)], fill=(138, 112, 64, 200), width=int(S * 0.008))

# sound waves above the book
for r, w, a in ((0.095, 0.018, 255), (0.165, 0.016, 190), (0.235, 0.014, 120)):
    box = [cx - S*r, top - S*0.06 - S*r, cx + S*r, top - S*0.06 + S*r]
    d.arc(box, start=230, end=310, fill=GOLD_L[:3] + (a,), width=int(S * w))

# clip everything to the squircle once more
final = Image.new("RGBA", (S, S), (0, 0, 0, 0))
final.alpha_composite(img)

def png(size):
    buf = io.BytesIO()
    final.resize((size, size), Image.LANCZOS).save(buf, "PNG")
    return buf.getvalue()

entries = [(b"ic07", 128), (b"ic13", 256), (b"ic08", 256), (b"ic14", 512),
           (b"ic09", 512), (b"ic10", 1024), (b"ic11", 32), (b"ic12", 64)]
chunks = b"".join(t + struct.pack(">I", len(p) + 8) + p for t, p in ((t, png(s)) for t, s in entries))
open("Resources/AppIcon.icns", "wb").write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
final.resize((512, 512), Image.LANCZOS).save("scripts/icon_preview.png")
print("wrote Resources/AppIcon.icns")
