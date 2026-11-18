# Fillsign working-copy format (version 1)

A Fillsign **working copy** is an ordinary PDF that other readers display normally. Fillsign also stores an **editable record** inside it, so it can restore every added item when the file is reopened (FR-9). Final copies never contain the record (FR-9.4).

Code: `src/Fillsign.Core/Session/` (`SessionStore`, `SessionJson`, `SessionRecord`).

## Where the record lives

The record is the last **incremental update** in the file:

```
<the PDF as Fillsign saved it>          ← "content": length + SHA-256 in the record
N 0 obj
<</Type/FillsignSession/Filter/FlateDecode/Length L>>
stream
<zlib-compressed JSON record>
endstream
endobj
xref
N 1
OOOOOOOOOO 00000 n
trailer
<</Size N+1 /Root … /Info … /ID … /Encrypt … /Prev P /FillsignSession N 0 R>>
startxref
X
%%EOF
```

- `/FillsignSession` is a private trailer key. Other readers ignore it, and the update adds no visible object, so nothing shows in attachment panels.
- `/Root`, `/Info`, `/ID` and `/Encrypt` are copied from the previous trailer. `/Prev` points at the previous cross-reference section (classic table or xref stream).
- Saving again strips Fillsign's update first, so the file doesn't keep growing (FR-9.8).

## Detecting changes by other apps (FR-9.6)

PDFium and some other writers keep unknown trailer keys when they resave a file, so the key surviving proves nothing. On open, Fillsign checks:

1. `content.sha256` matches the first `content.length` bytes of the file, and
2. Fillsign's update is the only one after those bytes, and
3. `base.sha256` matches the first `base.length` bytes (the clean original, needed to restore clean pages).

All three → **Intact**: restore. Otherwise → **ChangedElsewhere**: open the file as it is now and offer restoring only as a deliberate choice. If the record can't be read → **Damaged**: open as a normal PDF (FR-9.5).

## The JSON record

UTF-8 JSON. The reader is strict. It rejects the whole record on an unknown or missing property, a wrong type, an out-of-range number, a duplicate id or a dangling reference. Limits: 64 MB per record, 10,000 pages, 100,000 items, 10,000 characters per text item, 200,000 pen points, 8 MB per signature image.

```json
{
  "format": "fillsign-session",
  "version": 1,
  "base": { "length": 12345, "sha256": "<64 lowercase hex>" },
  "content": { "length": 23456, "sha256": "<64 lowercase hex>" },
  "sourcePageCount": 4,
  "pages": [ { "id": "<32 hex>", "source": 0, "rotation": 90 } ],
  "images": [ { "id": "<32 hex>", "png": "<base64>", "pixelWidth": 480, "pixelHeight": 160 } ],
  "items": [ … ]
}
```

- `pages` is the page list in display order. `source` is a 0-based page index in the clean original. `rotation` is the extra clockwise rotation added in Fillsign (0, 90, 180 or 270).
- Every item has `type`, `id`, `page` (a page `id`), and `x`, `y`. Coordinates are PDF points from the top-left of the page as displayed, with y pointing down.

| `type` | Other properties |
|---|---|
| `text` | `text` (lines separated by `\n`), `fontSize` (6–72), `color` (`#rrggbb`). `x`, `y` is the top-left of the first line; each line is `1.2 × fontSize` tall with its baseline at the line's top plus the Noto Sans ascent, unkerned |
| `mark` | `kind` (`tick` or `cross`), `size` (6–72), `color` |
| `signature` | `kind` (`signature` or `initials`), `image` (an `images` id), `width`, `height` |
| `shape` | `shape` (`rectangle`, `ellipse`, `line`, `arrow`, `highlight`, `whiteout`), `width`, `height`, `color`, `strokeWidth` (0.5–20), `flipX`, `flipY` |
| `ink` | `width`, `height`, `color`, `strokeWidth`, `strokes`: arrays of flat `[x0, y0, x1, y1, …]` fractions (0–1) of `width` and `height` |

## Versioning

`version` changes whenever a reader of the previous version would misread a record. Readers reject versions they don't know, and the file then opens as a normal PDF.
