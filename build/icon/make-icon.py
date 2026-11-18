"""Builds the Fillsign app icon files from the SVG sources in this folder.

Design B: a page with a ticked box and a signature kept inside the page, on the brand blue.
- fillsign.svg        Windows, Linux and the window icon (32 px and up)
- fillsign-small.svg  16 and 24 px, simplified so the tick and signature stay readable
- fillsign-macos.svg  the same artwork on Apple's icon grid

Run from the repo root on macOS 14+: python3 build/icon/make-icon.py
Needs Pillow, swift (Xcode command line tools) and iconutil. The generated files are committed,
so other platforms never need to run this.
"""
import os
import shutil
import subprocess
import tempfile
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join("src", "Fillsign.App", "Assets", "Icon")
SMALL_SIZES = (16, 24)


def render(svg: str, size: int, folder: str) -> str:
    out = os.path.join(folder, f"{os.path.splitext(svg)[0]}-{size}.png")
    subprocess.run(["swift", os.path.join(HERE, "render.swift"), os.path.join(HERE, svg), out, str(size)], check=True)
    return out


def general(size: int, folder: str) -> str:
    return render("fillsign-small.svg" if size in SMALL_SIZES else "fillsign.svg", size, folder)


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        # Linux and the in-app window icon.
        shutil.copy(general(1024, tmp), os.path.join(OUT, "fillsign.png"))
        shutil.copy(general(256, tmp), os.path.join(OUT, "fillsign-256.png"))
        shutil.copy(os.path.join(HERE, "fillsign.svg"), os.path.join(OUT, "fillsign.svg"))

        # Windows: every size drawn on its own, so 16 and 24 px use the simplified artwork.
        ico_sizes = (16, 24, 32, 48, 64, 128, 256)
        images = [Image.open(general(n, tmp)) for n in ico_sizes]
        images[-1].save(os.path.join(OUT, "fillsign.ico"), sizes=[(n, n) for n in ico_sizes], append_images=images[:-1])

        # macOS.
        iconset = os.path.join(tmp, "fillsign.iconset")
        os.makedirs(iconset)
        for n in (16, 32, 128, 256, 512):
            for scale, suffix in ((1, ""), (2, "@2x")):
                px = n * scale
                svg = "fillsign-small.svg" if px in SMALL_SIZES else "fillsign-macos.svg"
                shutil.copy(render(svg, px, tmp), os.path.join(iconset, f"icon_{n}x{n}{suffix}.png"))
        subprocess.run(["iconutil", "-c", "icns", iconset, "-o", os.path.join(OUT, "fillsign.icns")], check=True)


if __name__ == "__main__":
    main()
