# Third-party notices

Fillsign uses the following third-party components. Each is distributed under its own licence.

| Component | Use | Licence | Source |
|---|---|---|---|
| PDFium | PDF rendering (native library) | BSD-3-Clause / Apache-2.0 | https://pdfium.googlesource.com/pdfium |
| pdfium-binaries (bblanchon) | Prebuilt PDFium packages | MIT | https://github.com/bblanchon/pdfium-binaries |
| Avalonia | UI framework | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp | Graphics (via Avalonia); decoding photos and scans into pages | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzz / HarfBuzzSharp | Text shaping (via Avalonia) and font subsetting for saved PDFs | MIT (HarfBuzz: Old MIT) | https://github.com/harfbuzz/harfbuzz, https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MVVM helpers | MIT | https://github.com/CommunityToolkit/dotnet |
| Noto Sans | Bundled font, embedded as a subset in saved PDFs | SIL Open Font License 1.1 | https://github.com/notofonts |
| Inter (Avalonia.Fonts.Inter) | Interface font | SIL Open Font License 1.1 | https://github.com/rsms/inter |

Full licence texts for bundled fonts are shipped next to the font files (`OFL.txt`).
