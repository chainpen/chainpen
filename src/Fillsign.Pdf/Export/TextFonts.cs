using System.Collections.Concurrent;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Pdfium;
using SkiaSharp;

namespace Fillsign.Pdf.Export;

/// <summary>A font file that can draw some characters: bundled Noto, or one found on this computer.</summary>
internal sealed record FontSource(string Key, byte[] File, int FaceIndex, SKTypeface Typeface);

/// <summary>A stretch of a line drawn with one font, and how wide it is.</summary>
internal sealed record TextRun(FontSource Source, string Text, double Width);

/// <summary>
/// Which font draws each character of exported text (FS-612). Noto Sans first, as on screen; then the bundled Noto
/// symbols; then a font installed on this computer that has the character, so Chinese, Japanese or Korean text exports
/// as real text instead of empty boxes. Only fonts whose licence allows embedding are used. Scripts that need shaping
/// (Arabic, Devanagari) get their letters but not joined forms: PDFium lays out characters one by one.
/// </summary>
internal static class TextFontChoice
{
    private static readonly Lazy<FontSource> s_noto = new(() => Bundled(BundledFonts.RegularFace));
    private static readonly Lazy<FontSource> s_symbols = new(() => Bundled(BundledFonts.SymbolsFace));
    private static readonly ConcurrentDictionary<string, FontSource?> s_system = new();

    public static FontSource Primary => s_noto.Value;

    /// <summary>The line in runs of one font each, with their widths at <paramref name="fontSize"/> (no kerning, as on screen).</summary>
    public static IReadOnlyList<TextRun> Runs(string line, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(line);
        var runs = new List<TextRun>();
        FontSource? current = null;
        var text = new System.Text.StringBuilder();
        foreach (System.Text.Rune rune in line.EnumerateRunes())
        {
            FontSource source = For(rune.Value);
            if (current is not null && source != current)
            {
                runs.Add(Measured(current, text.ToString(), fontSize));
                text.Clear();
            }

            current = source;
            text.Append(rune);
        }

        if (current is not null && text.Length > 0)
        {
            runs.Add(Measured(current, text.ToString(), fontSize));
        }

        return runs;
    }

    /// <summary>The font for one character; Noto when nothing has it (it then shows a box, as before).</summary>
    public static FontSource For(int codepoint)
    {
        if (char.IsWhiteSpace((char)Math.Min(codepoint, char.MaxValue)) || s_noto.Value.Typeface.ContainsGlyph(codepoint))
        {
            return s_noto.Value;
        }

        if (s_symbols.Value.Typeface.ContainsGlyph(codepoint))
        {
            return s_symbols.Value;
        }

        return SystemFont(codepoint) ?? s_noto.Value;
    }

    private static TextRun Measured(FontSource source, string text, double fontSize)
    {
        double width = ReferenceEquals(source, s_noto.Value)
            ? NotoTextMeasurer.Instance.MeasureLineWidth(text, fontSize)
            : new SKFont(source.Typeface, (float)fontSize).MeasureText(text);
        return new TextRun(source, text, width);
    }

    private static FontSource? SystemFont(int codepoint) => s_byCharacter.GetOrAdd(codepoint, FindSystemFont);

    // Which installed font draws a character, worked out once per character.
    private static readonly ConcurrentDictionary<int, FontSource?> s_byCharacter = new();

    /// <summary>
    /// The system's own pick for the character if PDF can embed it, else the first installed family that has it with
    /// TrueType or CFF outlines. Some system fonts (macOS 15's PingFang) keep their outlines in a table PDF can't use.
    /// </summary>
    private static FontSource? FindSystemFont(int codepoint)
    {
        SKTypeface? match = SKFontManager.Default.MatchCharacter(codepoint);
        if (match is not null && HasEmbeddableOutlines(match) && Source(match) is { } matched)
        {
            return matched;
        }

        match?.Dispose();
        foreach (string family in SKFontManager.Default.FontFamilies.Order(StringComparer.Ordinal))
        {
            SKTypeface? candidate = SKFontManager.Default.MatchFamily(family);
            if (candidate is not null && candidate.ContainsGlyph(codepoint) && HasEmbeddableOutlines(candidate) && Source(candidate) is { } found)
            {
                return found;
            }

            candidate?.Dispose();
        }

        return null;
    }

    private static bool HasEmbeddableOutlines(SKTypeface typeface)
    {
        const uint Glyf = 0x676C7966, Cff = 0x43464620;
        return typeface.GetTableTags() is { } tags && (tags.Contains(Glyf) || tags.Contains(Cff));
    }

    /// <summary>The typeface's file, kept with the typeface itself for measuring (Skia can't always rebuild one from the file).</summary>
    private static FontSource? Source(SKTypeface typeface)
    {
        string key = $"{typeface.FamilyName}|{typeface.FontStyle.Weight}|{typeface.FontStyle.Slant}";
        if (s_system.TryGetValue(key, out FontSource? known))
        {
            typeface.Dispose();
            return known;
        }

        using SKStreamAsset? stream = typeface.OpenStream(out int faceIndex);
        byte[]? bytes = stream is { Length: > 0 } ? ReadAll(stream) : null;
        if (bytes is null)
        {
            return null;
        }

        return s_system.GetOrAdd(key, new FontSource(key, bytes, faceIndex, typeface));
    }

    private static byte[]? ReadAll(SKStreamAsset stream)
    {
        var bytes = new byte[stream.Length];
        int read = stream.Read(bytes, bytes.Length);
        return read == bytes.Length ? bytes : null;
    }

    private static FontSource Bundled(string face)
    {
        byte[] bytes = BundledFonts.Load(face);
        SKTypeface typeface = SKTypeface.FromData(SKData.CreateCopy(bytes)) ?? throw new InvalidOperationException($"The {face} font could not be loaded.");
        return new FontSource(face, bytes, 0, typeface);
    }
}

/// <summary>The fonts embedded in one exported document: each source used, cut down to the characters it draws.</summary>
internal sealed class EmbeddedFonts
{
    private readonly Dictionary<FontSource, PdfiumFont> _fonts = [];

    private EmbeddedFonts()
    {
    }

    /// <summary>Embeds a subset of every font these lines need. Fonts that can't be embedded fall back to Noto.</summary>
    public static EmbeddedFonts For(PdfiumDocument pdf, IEnumerable<string> lines)
    {
        var fonts = new EmbeddedFonts();
        var characters = new Dictionary<FontSource, System.Text.StringBuilder>();
        foreach (string line in lines)
        {
            foreach (System.Text.Rune rune in line.EnumerateRunes())
            {
                FontSource source = TextFontChoice.For(rune.Value);
                if (!characters.TryGetValue(source, out System.Text.StringBuilder? text))
                {
                    characters[source] = text = new System.Text.StringBuilder();
                }

                text.Append(rune);
            }
        }

        foreach ((FontSource source, System.Text.StringBuilder text) in characters)
        {
            if (pdf.TryLoadSubsetFont(source.File, text.ToString(), source.FaceIndex) is { } font)
            {
                fonts._fonts[source] = font;
            }
        }

        return fonts;
    }

    public bool IsEmpty => _fonts.Count == 0;

    /// <summary>The embedded font for a run; Noto (showing boxes, as before) when its own font couldn't be embedded.</summary>
    public PdfiumFont? FontFor(FontSource source) =>
        _fonts.TryGetValue(source, out PdfiumFont font) ? font
        : _fonts.TryGetValue(TextFontChoice.Primary, out PdfiumFont noto) ? noto
        : null;

    /// <summary>The font the line layout (baseline) is measured with: Noto, as on screen.</summary>
    public PdfiumFont? Primary => FontFor(TextFontChoice.Primary) ?? _fonts.Values.Cast<PdfiumFont?>().FirstOrDefault();
}
