# Chainpen working-copy format (version 1)

A Chainpen **working copy** is an ordinary PDF that other readers display normally. Chainpen also stores an **editable record** inside it, so it can restore every added item when the file is reopened. Final copies never contain the record.

Code: `src/Chainpen.Core/Session/` (`SessionStore`, `SessionJson`, `SessionRecord`).

## Where the record lives

The record is the last **incremental update** in the file. Before it, the file holds the clean original and PDFium's incremental update with the finished pages, so other readers show every item drawn in:

```
<the clean original>                    ← "base": every opened and added page, unencrypted, nothing drawn in
<PDFium's incremental update>           ← pages reordered, rotated and duplicated; items drawn in as PDF content
                                           (the two together are "content")
N 0 obj
<</Type/ChainpenSession/Filter/FlateDecode/Length L>>
stream
<zlib-compressed JSON record>
endstream
endobj
xref
N 1
OOOOOOOOOO 00000 n
trailer
<</Size N+1 /Root … /Info … /ID … /Encrypt … /Prev P /ChainpenSession N 0 R>>
startxref
X
%%EOF
```

- `/ChainpenSession` is a private trailer key. Other readers ignore it, and the update adds no visible object, so nothing shows in attachment panels.
- `/Root`, `/Info`, `/ID` and `/Encrypt` are copied from the previous trailer. `/Prev` points at the previous cross-reference section (classic table or xref stream).
- Saving again starts from the clean original, not from the last save, so the file doesn't keep growing.
- Code that writes it: `src/Chainpen.Pdf/Export/DocumentWriter.cs`. A final copy (Export) is written by the same code as a full, compact file with no record and none of the original's unused pages.

## Detecting changes by other apps

PDFium and some other writers keep unknown trailer keys when they resave a file, so the key surviving proves nothing. On open, Chainpen checks:

1. `content.sha256` matches the first `content.length` bytes of the file, and
2. Chainpen's update is the only one after those bytes, and
3. `base.sha256` matches the first `base.length` bytes (the clean original, needed to restore clean pages).

All three → **Intact**: restore. Otherwise → **ChangedElsewhere**: open the file as it is now and offer restoring only as a deliberate choice. If the record can't be read → **Damaged**: open as a normal PDF.

## The JSON record

UTF-8 JSON. The reader is strict. It rejects the whole record on an unknown or missing property, a wrong type, an out-of-range number, a duplicate id or a dangling reference. Limits: 64 MB per record, 10,000 pages, 100,000 items, 10,000 characters per text item, 200,000 pen points, 8 MB per signature image.

```json
{
  "format": "chainpen-session",
  "version": 1,
  "base": { "length": 12345, "sha256": "<64 lowercase hex>" },
  "content": { "length": 23456, "sha256": "<64 lowercase hex>" },
  "sourcePageCount": 4,
  "pages": [ { "id": "<32 hex>", "source": 0, "rotation": 90 } ],
  "images": [ { "id": "<32 hex>", "png": "<base64>", "pixelWidth": 480, "pixelHeight": 160 } ],
  "items": [ … ],
  "fields": [ { "name": "A.NOM", "value": "Lee" } ]
}
```

- `pages` is the page list in display order. `source` is a 0-based page index in the clean original. `rotation` is the extra clockwise rotation added in Chainpen (0, 90, 180 or 270).
- `fields` (optional) holds values typed into the PDF's own form fields, by full field name: the text, the chosen option (a multi-select list's chosen options one per line, separated by `\n`), or for check boxes and radio buttons their on-value or `Off`. Only fields changed in Chainpen are listed; it's left out when there are none. Names are up to 1,000 characters and unique.
- Every item has `type`, `id`, `page` (a page `id`), and `x`, `y`. Coordinates are PDF points from the top-left of the page as displayed, with y pointing down.

| `type` | Other properties |
|---|---|
| `text` | `text` (lines separated by `\n`), `fontSize` (6–72), `color` (`#rrggbb`); optional `bold` (`true`) and `align` (`centre` or `right`), left out for regular, left-aligned text. `x`, `y` is the top-left of the first line; each line is `1.2 × fontSize` tall with its baseline at the line's top plus the Noto Sans ascent, unkerned. Aligned lines start where a shorter line leaves space within the longest line's width. Bold is the same glyphs outlined with a stroke `0.05 × fontSize` wide, so it measures like regular text |
| `mark` | `kind` (`tick` or `cross`), `size` (6–72), `color` |
| `signature` | `kind` (`signature` or `initials`), `image` (an `images` id), `width`, `height` |
| `picture` | `image` (an `images` id), `width`, `height`. A picture pasted from another app; readers that predate it reject the record as unknown |
| `shape` | `shape` (`rectangle`, `ellipse`, `line`, `arrow`, `highlight`, `whiteout`), `width`, `height`, `color`, `strokeWidth` (0.5–20), `flipX`, `flipY` |
| `ink` | `width`, `height`, `color`, `strokeWidth`, `strokes`: arrays of flat `[x0, y0, x1, y1, …]` fractions (0–1) of `width` and `height` |

## Versioning

`version` changes whenever a reader of the previous version would misread a record. Readers reject versions they don't know, and the file then opens as a normal PDF.

---

# Saved signatures file (version 1)

Signatures and initials the person chose to keep live in `signatures.dat` in the app data folder (`~/Library/Application Support/Chainpen`, `%APPDATA%\Chainpen`, `$XDG_CONFIG_HOME/Chainpen`). It never leaves the computer, and it isn't part of any PDF.

Code: `src/Chainpen.App/Platform/Signatures/` (`SavedSignatureStore`, the key vaults).

## Layout

```
"FSSIG" 0x01      magic and format version (6 bytes)
mode              0 = plain JSON follows, 1 = encrypted (1 byte)
mode 1 only:      nonce (12 bytes), tag (16 bytes), ciphertext
```

- Encryption is AES-256-GCM. The associated data is the 7-byte header, so the mode can't be changed without the tag failing.
- The 32-byte key lives in the system's secret store: the login Keychain on macOS (generic password, service `Chainpen`, account `Saved signatures key`), a DPAPI-protected `signatures.key` beside the file on Windows, and the Secret Service via `secret-tool` on Linux (attributes `application=chainpen`, `purpose=saved-signatures-key`). Without a secret store (Linux with no keyring), the file is plain.
- The file is created readable and writable by its owner only, and replaced atomically (written to `.tmp`, then moved).

## Reading

- **Key store locked or refused** (for example Keychain access denied): nothing is shown and the file is left untouched; keeping new signatures is off until the key is available again.
- **No key can open it** (the tag fails, the key is gone, or the file is damaged): it's renamed `signatures.dat.unreadable` and the person starts with none.
- A plain file is encrypted the next time it's written, once a secret store is available.

## The JSON

```json
{
  "items": [
    { "id": "<32 hex>", "kind": "signature", "method": "drawn", "png": "<base64>", "pixelWidth": 480, "pixelHeight": 120, "lastUsed": "2026-11-18T02:15:00+00:00" }
  ]
}
```

- `kind` is `signature` or `initials`; `method` is `drawn`, `typed` or `photo`. Records with an unknown value, a bad id or size, or a PNG over 8 MB are skipped.
- Up to 6 of each kind. Keeping a seventh drops the one with the oldest `lastUsed`, and placing a saved one updates its `lastUsed`.
- `id` is also the image id placed items refer to in a working copy, so a placed signature and its saved original share an id. Removing a saved one doesn't touch documents.
