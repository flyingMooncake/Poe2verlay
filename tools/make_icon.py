"""Builds assets/poe2verlay.ico from assets/logo.png.

    python3 tools/make_icon.py                 # icon from assets/logo.png
    python3 tools/make_icon.py emblem.png      # new logo: emblem on black -> transparent assets/logo.png, then the icon

The emblem is too detailed for the smallest sizes (tray, title bar), so 16-24 px show only its gold "2".
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
LOGO = os.path.join(ROOT, "assets", "logo.png")
ICON = os.path.join(ROOT, "assets", "poe2verlay.ico")
LOGO_SIZE = 512
# The gold "2" inside assets/logo.png, as fractions of its side.
DIGIT = (0.268, 0.273, 0.696, 0.702)


def remove_black(src: Image.Image) -> Image.Image:
    """Black background -> transparent. The glow keeps a soft alpha, the stone and gold shapes stay solid."""
    a = np.asarray(src.convert("RGB")).astype(np.float32) / 255
    m = a.max(axis=2)
    soft = np.clip((m - 0.03) / 0.97, 0, 1)
    # Solid body with its dark cracks closed up, so they don't turn see-through.
    body = Image.fromarray(((m > 0.28) * 255).astype(np.uint8))
    body = body.filter(ImageFilter.MaxFilter(7)).filter(ImageFilter.MinFilter(7)).filter(ImageFilter.GaussianBlur(1))
    alpha = np.maximum(soft, np.asarray(body).astype(np.float32) / 255)
    rgb = np.clip(a / np.maximum(alpha[..., None], 1e-4), 0, 1)
    img = Image.fromarray(np.dstack([rgb * 255, alpha * 255]).round().astype(np.uint8), "RGBA")

    img = img.crop(img.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox())
    side = max(img.size) + 16
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
    return square.resize((LOGO_SIZE, LOGO_SIZE), Image.LANCZOS)


def gold_only(img: Image.Image) -> Image.Image:
    """Drops the grey stone (edges of the P and V) next to the saturated gold digit."""
    a = np.asarray(img).astype(np.float32)
    sat = np.asarray(img.convert("RGB").convert("HSV")).astype(np.float32)[..., 1] / 255
    a[..., 3] *= np.clip((sat - 0.35) / 0.2, 0, 1)
    return Image.fromarray(a.round().astype(np.uint8), "RGBA")


def main() -> None:
    if len(sys.argv) > 1:
        remove_black(Image.open(sys.argv[1])).save(LOGO, optimize=True)
        print("wrote", LOGO)
    logo = Image.open(LOGO).convert("RGBA")
    s = logo.width
    digit = gold_only(logo.crop(tuple(round(f * s) for f in DIGIT)))

    big = [logo.resize((n, n), Image.LANCZOS) for n in (32, 40, 48, 64, 128)]
    small = [digit.resize((n, n), Image.LANCZOS) for n in (16, 20, 24)]
    logo.resize((256, 256), Image.LANCZOS).save(ICON, format="ICO", append_images=small + big,
                                                sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    print("wrote", ICON)


if __name__ == "__main__":
    main()
