"""Draws assets/poe2verlay.ico: a gold coin with a falling meteor streak."""
import os
import sys

from PIL import Image, ImageDraw

S = 1024  # draw large, downsample for anti-aliasing


def draw() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = 40
    # coin rim + face (radial-ish gradient by concentric circles)
    d.ellipse([pad, pad, S - pad, S - pad], fill=(92, 54, 10, 255))
    steps = 60
    for i in range(steps):
        t = i / steps
        inset = pad + 40 + t * 380
        c = (int(196 + 59 * t), int(130 + 95 * t), int(30 + 90 * t), 255)
        d.ellipse([inset, inset, S - inset, S - inset], fill=c)
    # meteor streak
    for i in range(18):
        t = i / 18
        w = 30 + 110 * t
        x0, y0 = 250 + 260 * t, 250 + 260 * t
        d.ellipse([x0 - w / 2, y0 - w / 2, x0 + w / 2, y0 + w / 2], fill=(255, 245, 220, int(90 + 140 * t)))
    d.ellipse([560 - 120, 560 - 120, 560 + 120, 560 + 120], fill=(70, 32, 8, 255))
    d.ellipse([560 - 82, 560 - 82, 560 + 82, 560 + 82], fill=(255, 120, 40, 255))
    d.ellipse([540 - 34, 540 - 34, 540 + 34, 540 + 34], fill=(255, 230, 160, 255))
    return img


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "assets", "poe2verlay.ico")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    big = draw()
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    big.resize((256, 256), Image.LANCZOS).save(out, format="ICO", sizes=sizes)
    big.resize((256, 256), Image.LANCZOS).save(os.path.splitext(out)[0] + ".png")
    print("wrote", out)


if __name__ == "__main__":
    main()
