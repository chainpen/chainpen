#!/usr/bin/env python3
"""Makes src/Chainpen.App/Assets/practice-form.pdf: a one-page form that looks scanned.

The practice form teaches the way Chainpen fills a scan or photo (choose a tool, then click where the answer goes),
so it is an image of a page, not a PDF with text or fields: clicking a box with Select does nothing, just as on a
real scan. Run on macOS (it uses the system's Arial) with Pillow installed:

    python3 build/make-practice-form.py

It makes one form per interface language (practice-form.pdf in English, practice-form.de.pdf, practice-form.ja.pdf),
all with the box, name line and signature line in the same places. It prints where those ended up, in PDF points
from the top left; the practice guide (EditorViewModel.Guide.cs) points at them, and quotes the form's wording
(Editor_Guide* in Strings*.resx), so change both together.
"""

import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

DPI = 150
WIDTH_PT, HEIGHT_PT = 595, 842  # A4
TILT_DEGREES = 0.7  # A scan is never quite straight.
ASSETS = Path(__file__).resolve().parent.parent / "src" / "Chainpen.App" / "Assets"
FONTS = Path("/System/Library/Fonts/Supplemental")
JAPANESE = {"regular": "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", "bold": "/System/Library/Fonts/ヒラギノ角ゴシック W6.ttc"}

TEXT = {
    "en": {
        "note_title": "PRACTICE FORM: an example of a scanned paper form",
        "note": [
            "Its boxes and lines are only part of the picture, so clicking them does nothing.",
            "In Chainpen, choose a tool first (Tick, Text or Sign), then click where each answer goes.",
        ],
        "title": "Riverside Community Garden",
        "subtitle": "Volunteer sign-up form",
        "instruction": "Please tick the boxes that apply.",
        "rows": ["I agree to the volunteer guidelines", "Send me the monthly newsletter", "I am over 18"],
        "name": "Name",
        "signature": "Signature",
        "footer": "Hand this form to the garden coordinator. Thank you!",
    },
    "de": {
        "note_title": "ÜBUNGSFORMULAR: ein Beispiel für ein gescanntes Papierformular",
        "note": [
            "Kästchen und Linien sind nur Teil des Bildes, ein Klick darauf bewirkt nichts.",
            "Wählen Sie in Chainpen zuerst ein Werkzeug und klicken Sie dann auf die richtige Stelle.",
        ],
        "title": "Gemeinschaftsgarten am Fluss",
        "subtitle": "Anmeldung für Freiwillige",
        "instruction": "Bitte kreuzen Sie Zutreffendes an.",
        "rows": ["Ich stimme den Richtlinien für Freiwillige zu", "Bitte senden Sie mir den monatlichen Newsletter", "Ich bin über 18 Jahre alt"],
        "name": "Name",
        "signature": "Unterschrift",
        "footer": "Bitte geben Sie dieses Formular bei der Gartenkoordination ab. Vielen Dank!",
    },
    "ja": {
        "note_title": "練習用フォーム：スキャンした紙の書類の例です",
        "note": [
            "チェックボックスや線は画像の一部なので、クリックしても何も起こりません。",
            "Chainpenでは、まずツール（チェック、テキスト、署名）を選んでから、記入する場所をクリックします。",
        ],
        "title": "リバーサイド・コミュニティガーデン",
        "subtitle": "ボランティア申込書",
        "instruction": "当てはまるものにチェックを入れてください。",
        "rows": ["ボランティアの指針に同意します", "月刊ニュースレターを受け取る", "18歳以上です"],
        "name": "氏名",
        "signature": "署名",
        "footer": "この用紙をガーデンのコーディネーターにお渡しください。ありがとうございました。",
    },
}


def px(points: float) -> int:
    return round(points * DPI / 72)


def make(language: str) -> None:
    text = TEXT[language]

    def font(name: str, points: float) -> ImageFont.FreeTypeFont:
        if language == "ja":
            return ImageFont.truetype(JAPANESE["bold" if "Bold" in name else "regular"], px(points))
        return ImageFont.truetype(str(FONTS / name), px(points))

    def fitted(words: str, name: str, points: float, room: float) -> ImageFont.FreeTypeFont:
        # Longer languages get a slightly smaller size rather than running off the paper.
        while points > 6 and draw.textlength(words, font=font(name, points)) > px(room):
            points -= 0.25
        return font(name, points)

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
    room = WIDTH_PT - (2 * margin) - 24
    draw.text((px(margin + 12), px(54)), text["note_title"], font=fitted(text["note_title"], "Arial Bold.ttf", 11, room), fill=ink)
    for i, line in enumerate(text["note"]):
        draw.text((px(margin + 12), px(72 + (i * 15))), line, font=fitted(line, "Arial.ttf", 10, room), fill=ink)

    shift = 80  # The form starts below the note.
    draw.text((px(margin), px(70 + shift)), text["title"], font=bold, fill=ink)
    draw.text((px(margin), px(96 + shift)), text["subtitle"], font=font("Arial.ttf", 13), fill=ink)
    draw.line((px(margin), px(118 + shift), px(WIDTH_PT - margin), px(118 + shift)), fill=ink, width=2)

    draw.text((px(margin), px(140 + shift)), text["instruction"], font=regular, fill=ink)
    box = 12
    rows = text["rows"]
    for i, label in enumerate(rows):
        top = 170 + shift + (i * 30)
        draw.rectangle((px(margin), px(top), px(margin + box), px(top + box)), outline=ink, width=2)
        draw.text((px(margin + box + 10), px(top - 1)), label, font=regular, fill=ink)
        if i == 0:
            targets["box"] = (margin, top, box, box)

    def blank(label: str, top: float, key: str) -> None:
        draw.text((px(margin), px(top - 13)), label, font=fitted(label, "Arial.ttf", 11, 64), fill=ink)
        start = margin + 70
        draw.line((px(start), px(top), px(WIDTH_PT - margin - 160), px(top)), fill=ink, width=2)
        targets[key] = (start, top - 22, WIDTH_PT - margin - 160 - start, 26)

    blank(text["name"], 300 + shift, "name")
    blank(text["signature"], 360 + shift, "signature")
    draw.text((px(margin), px(400 + shift)), text["footer"], font=small, fill=ink)

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
    out = ASSETS / ("practice-form.pdf" if language == "en" else f"practice-form.{language}.pdf")
    page.save(out, "PDF", resolution=DPI, quality=72)

    # Where the targets moved to after the tilt: rotate() turns anticlockwise about the centre.
    angle = math.radians(TILT_DEGREES)
    cx, cy = WIDTH_PT / 2, HEIGHT_PT / 2
    for key, (x, y, w, h) in targets.items():
        mx, my = x + (w / 2) - cx, y + (h / 2) - cy
        rx = cx + (mx * math.cos(angle)) + (my * math.sin(angle))
        ry = cy - (mx * math.sin(angle)) + (my * math.cos(angle))
        print(f"{language} {key}: x {rx - (w / 2):.0f}, y {ry - (h / 2):.0f}, w {w:.0f}, h {h:.0f}")
    print(f"{out} {out.stat().st_size // 1024} KB")


if __name__ == "__main__":
    for code in TEXT:
        make(code)
