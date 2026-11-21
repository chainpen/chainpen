#!/usr/bin/env python3
"""Makes src/Fillsign.App/Assets/practice-form.pdf: a one-page form that looks scanned.

The practice form teaches the way Fillsign fills a scan or photo (choose a tool, then click where the answer goes),
so it is an image of a page, not a PDF with text or fields: clicking a box with Select does nothing, just as on a
real scan. Run on macOS (it uses the system's Arial) with Pillow installed:

    python3 build/make-practice-form.py

It prints where the box, name line and signature line ended up, in PDF points from the top left; the practice
guide (EditorViewModel.Guide.cs) points at those places.
"""

import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

DPI = 150
WIDTH_PT, HEIGHT_PT = 595, 842  # A4
TILT_DEGREES = 0.7  # A scan is never quite straight.
OUT = Path(__file__).resolve().parent.parent / "src" / "Fillsign.App" / "Assets" / "practice-form.pdf"
FONTS = Path("/System/Library/Fonts/Supplemental")


def px(points: float) -> int:
    return round(points * DPI / 72)


def font(name: str, points: float) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), px(points))


def main() -> None:
    random.seed(967)
    width, height = px(WIDTH_PT), px(HEIGHT_PT)
    page = Image.new("L", (width, height), 255)
    draw = ImageDraw.Draw(page)
    ink = 40
    regular, bold, small = font("Arial.ttf", 11), font("Arial Bold.ttf", 18), font("Arial.ttf", 9)

    targets: dict[str, tuple[float, float, float, float]] = {}
    margin = 72

    # Printed on the form itself, so it's the first thing read and it's still there if the guide is skipped.
    draw.rectangle((px(margin), px(44), px(WIDTH_PT - margin), px(108)), fill=228, outline=ink, width=2)
    draw.text((px(margin + 12), px(54)), "PRACTICE FORM: an example of a scanned paper form", font=font("Arial Bold.ttf", 11), fill=ink)
    note = [
        "Its boxes and lines are only part of the picture, so clicking them does nothing.",
        "In Fillsign, choose a tool first (Tick, Text or Sign), then click where each answer goes.",
    ]
    for i, line in enumerate(note):
        draw.text((px(margin + 12), px(72 + (i * 15))), line, font=font("Arial.ttf", 10), fill=ink)

    shift = 80  # The form starts below the note.
    draw.text((px(margin), px(70 + shift)), "Riverside Community Garden", font=bold, fill=ink)
    draw.text((px(margin), px(96 + shift)), "Volunteer sign-up form", font=font("Arial.ttf", 13), fill=ink)
    draw.line((px(margin), px(118 + shift), px(WIDTH_PT - margin), px(118 + shift)), fill=ink, width=2)

    draw.text((px(margin), px(140 + shift)), "Please tick the boxes that apply.", font=regular, fill=ink)
    box = 12
    rows = ["I agree to the volunteer guidelines", "Send me the monthly newsletter", "I am over 18"]
    for i, label in enumerate(rows):
        top = 170 + shift + (i * 30)
        draw.rectangle((px(margin), px(top), px(margin + box), px(top + box)), outline=ink, width=2)
        draw.text((px(margin + box + 10), px(top - 1)), label, font=regular, fill=ink)
        if i == 0:
            targets["box"] = (margin, top, box, box)

    def blank(label: str, top: float, key: str) -> None:
        draw.text((px(margin), px(top - 13)), label, font=regular, fill=ink)
        start = margin + 70
        draw.line((px(start), px(top), px(WIDTH_PT - margin - 160), px(top)), fill=ink, width=2)
        targets[key] = (start, top - 22, WIDTH_PT - margin - 160 - start, 26)

    blank("Name", 300 + shift, "name")
    blank("Signature", 360 + shift, "signature")
    draw.text((px(margin), px(400 + shift)), "Hand this form to the garden coordinator. Thank you!", font=small, fill=ink)

    # Paper: a little uneven light, a faint grey cast and specks, as a flatbed or phone camera gives.
    shade = Image.linear_gradient("L").resize((width, height)).point(lambda v: 243 - (v * 14 // 255))
    page = Image.composite(page, shade, page.point(lambda v: 255 if v < 200 else 0))
    noise = Image.effect_noise((width, height), 6)
    page = Image.blend(page, noise, 0.06)
    page = page.filter(ImageFilter.GaussianBlur(0.5))
    specks = ImageDraw.Draw(page)
    for _ in range(140):
        x, y = random.randrange(width), random.randrange(height)
        r = random.choice((1, 1, 1, 2))
        specks.ellipse((x, y, x + r, y + r), fill=random.randrange(120, 200))

    page = page.rotate(TILT_DEGREES, resample=Image.Resampling.BICUBIC, fillcolor=236)
    page.save(OUT, "PDF", resolution=DPI, quality=72)

    # Where the targets moved to after the tilt: rotate() turns anticlockwise about the centre.
    angle = math.radians(TILT_DEGREES)
    cx, cy = WIDTH_PT / 2, HEIGHT_PT / 2
    for key, (x, y, w, h) in targets.items():
        mx, my = x + (w / 2) - cx, y + (h / 2) - cy
        rx = cx + (mx * math.cos(angle)) + (my * math.sin(angle))
        ry = cy - (mx * math.sin(angle)) + (my * math.cos(angle))
        print(f"{key}: x {rx - (w / 2):.0f}, y {ry - (h / 2):.0f}, w {w:.0f}, h {h:.0f}")
    print(f"{OUT} {OUT.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
