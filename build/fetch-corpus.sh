#!/usr/bin/env bash
# Downloads the Phase 0 real-world PDF corpus (FS-021) from the pdf.js test suite,
# pinned to one commit. Files keep their upstream licences and are not committed
# (samples/corpus/ is git-ignored).
set -euo pipefail
PDFJS_SHA="60aefc5b51d526e39bbcbae1c6cb760763b65b0a"
DEST="$(cd "$(dirname "$0")/.." && pwd)/samples/corpus"
mkdir -p "$DEST"
FILES=(
  tracemonkey.pdf               # academic paper, many fonts
  TAMReview.pdf                 # multi-page article
  160F-2019.pdf                 # government form with AcroForm fields
  bug1937438_from_word.pdf      # exported from Microsoft Word
  alphatrans.pdf                # transparency groups
  hello_world_rotated.pdf       # /Rotate on page
  image-rotated-black-white-ratio.pdf
  empty_protected.pdf           # encrypted, empty user password
  crypt_filters.pdf             # encrypted with crypt filters
  encrypted-attachment.pdf      # encrypted embedded file
  helloworld-bad.pdf            # broken xref, needs repair
  Pages-tree-refs.pdf           # unusual page tree
  bad-PageLabels.pdf            # malformed page labels
  extractPages_null_in_array.pdf
  form_two_pages.pdf            # form fields across pages
  acroform_calculation_order.pdf
  annotation-link-text-popup.pdf # links and popups
  ArabicCIDTrueType.pdf         # RTL CID font
  cmykjpeg.pdf                  # CMYK JPEG image
  devicen.pdf                   # DeviceN colour space
  doc_1_3_pages.pdf
  close-path-bug.pdf
)
for f in "${FILES[@]}"; do
  [ -s "$DEST/$f" ] && continue
  curl -fsSL "https://raw.githubusercontent.com/mozilla/pdf.js/$PDFJS_SHA/test/pdfs/$f" -o "$DEST/$f"
  echo "fetched $f"
done
