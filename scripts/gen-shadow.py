"""Renders the overlay card's shadow as a nine-grid PNG.

    python scripts/gen-shadow.py

Two Gaussian layers, like real light: a wide, faint ambient shadow and a tight
contact shadow. Both fade to fully transparent before the image edge, so the
shadow never ends in a visible line. The margins must match OverlayView.Pad
(apps/windows/Overtone/Overlay/OverlayView.xaml.cs) and NineGrid in OverlayView.xaml.
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

PAD_L, PAD_T, PAD_R, PAD_B = 20, 14, 20, 26  # OverlayView.Pad
RADIUS = 15                                  # between the pill's 14 and the card's 16
CENTER = 24                                  # stretchable middle of the nine-grid
COLOR = (10, 6, 16)
# (sigma, y offset, peak opacity): ambient, then contact.
LAYERS = [(5.5, 6, 0.30), (1.8, 1.5, 0.22)]

w = PAD_L + 2 * RADIUS + CENTER + PAD_R
h = PAD_T + 2 * RADIUS + CENTER + PAD_B
SS = 4  # supersample the shape for a clean curve


def layer(sigma: float, dy: float, opacity: float) -> Image.Image:
    big = Image.new("L", (w * SS, h * SS), 0)
    d = ImageDraw.Draw(big)
    x0, y0 = PAD_L * SS, (PAD_T + dy) * SS
    x1, y1 = (w - PAD_R) * SS, (h - PAD_B + dy) * SS
    d.rounded_rectangle((x0, y0, x1 - 1, y1 - 1), radius=RADIUS * SS, fill=int(255 * opacity))
    small = big.resize((w, h), Image.LANCZOS)
    return small.filter(ImageFilter.GaussianBlur(sigma))


alpha = Image.new("L", (w, h), 0)
for sigma, dy, opacity in LAYERS:
    # Screen-combine the layers: a + b - a*b.
    alpha = ImageChops.screen(alpha, layer(sigma, dy, opacity))

edge = max(max(alpha.getpixel((x, 0)), alpha.getpixel((x, h - 1))) for x in range(w))
edge = max(edge, *(max(alpha.getpixel((0, y)), alpha.getpixel((w - 1, y))) for y in range(h)))
assert edge == 0, f"shadow reaches the image edge (alpha {edge}); widen the margins or lower sigma"

img = Image.new("RGBA", (w, h), COLOR + (0,))
img.putalpha(alpha)
out = Path(__file__).resolve().parent.parent / "apps" / "windows" / "Overtone" / "Assets" / "shadow.png"
img.save(out)
print(f"wrote {out} {w}x{h}; NineGrid {PAD_L + RADIUS},{PAD_T + RADIUS},{PAD_R + RADIUS},{PAD_B + RADIUS}")
