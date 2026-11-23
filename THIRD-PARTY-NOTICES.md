# Third-party notices

Chainpen uses the following third-party components. Each is distributed under its own licence.

| Component | Use | Licence | Source |
|---|---|---|---|
| PDFium | PDF rendering and writing (native library) | BSD-3-Clause (parts Apache-2.0) | https://pdfium.googlesource.com/pdfium |
| Libraries built into PDFium: FreeType, libjpeg-turbo, libpng, zlib, OpenJPEG, Little CMS, Anti-Grain Geometry, HarfBuzz, ICU, Abseil, Highway, fast_float, dragonbox, simdutf, LLVM libc | Fonts, images and text inside PDFium | FreeType Licence; IJG and BSD-3-Clause; libpng; zlib; BSD-2-Clause; MIT; AGG; Old MIT; Unicode v3; Apache-2.0; Boost-1.0 | Listed in `Licenses/PDFium` |
| pdfium-binaries (bblanchon) | Prebuilt PDFium packages | MIT | https://github.com/bblanchon/pdfium-binaries |
| Avalonia | UI framework | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp | Graphics (via Avalonia); decoding photos and scans into pages | MIT | https://github.com/mono/SkiaSharp |
| Skia, with libwebp, Expat and Wuffs | The graphics library inside SkiaSharp | BSD-3-Clause; BSD-3-Clause; MIT; Apache-2.0 | Listed in `Licenses/Skia` |
| HarfBuzz / HarfBuzzSharp | Text shaping (via Avalonia) and font subsetting for saved PDFs | MIT (HarfBuzz: Old MIT) | https://github.com/harfbuzz/harfbuzz, https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MVVM helpers | MIT | https://github.com/CommunityToolkit/dotnet |
| Velopack | Installers and updates | MIT | https://github.com/velopack/velopack |
| .NET runtime | Bundled with every release (self-contained) | MIT, with its own third-party notices | https://github.com/dotnet/runtime |
| Noto Sans | Bundled font, embedded as a subset in saved PDFs | SIL Open Font License 1.1 | https://github.com/notofonts |
| Inter (Avalonia.Fonts.Inter) | Interface font | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| Caveat | Bundled font for typed signatures | SIL Open Font License 1.1 | https://github.com/googlefonts/caveat |
| Dancing Script | Bundled font for typed signatures (Reserved Font Name "Dancing Script"; shipped unmodified) | SIL Open Font License 1.1 | https://github.com/googlefonts/DancingScript |
| Kalam | Bundled font for typed signatures | SIL Open Font License 1.1 | https://github.com/itfoundry/kalam |

Every component above allows use in a commercial, paid product. None is under the GPL or LGPL. Their licences ask
that their copyright and licence notices travel with the app, which the `Licenses` folder does. The FreeType and IJG
licences also ask for a credit in the documentation:

- Portions of this software are copyright © 2026 The FreeType Project (www.freetype.org). All rights reserved.
- This software is based in part on the work of the Independent JPEG Group.

When exported text uses a font installed on the computer (characters Noto Sans doesn't have), only a subset of it is
embedded in the document, and only if the font's own embedding permission allows it.

Test-only packages (xUnit, Avalonia.Headless) aren't shipped.

Full licence texts are shipped in the app's `Licenses` folder (source: `src/Chainpen.App/Licenses`), with the bundled fonts' licences next to the font files (`OFL.txt`, and `*-OFL.txt` for the handwriting fonts).
