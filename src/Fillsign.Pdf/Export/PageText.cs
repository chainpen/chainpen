using Fillsign.Core.Finding;
using Fillsign.Core.Gaps;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Export;

/// <summary>A page's own text where it's shown: its characters with their boxes in display coordinates (FS-974).</summary>
public static class PageText
{
    /// <summary>
    /// <paramref name="characters"/> (from <see cref="PdfiumDocument.GetCharacters"/>) placed on the page as it's
    /// shown with <paramref name="rotation"/> added to its own. Spaces PDFium inferred keep their empty box.
    /// </summary>
    public static PageCharacter[] OnPage(IReadOnlyList<PdfCharacter> characters, SourcePage source, Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(characters);
        var transform = new PageTransform(source, rotation);
        var placed = new PageCharacter[characters.Count];
        for (int i = 0; i < characters.Count; i++)
        {
            PdfCharacter character = characters[i];
            bool hasBox = character.Right > character.Left && character.Top > character.Bottom;
            placed[i] = new PageCharacter(
                character.Code,
                hasBox ? PageRect.FromCorners(transform.ToDisplay(character.Left, character.Top), transform.ToDisplay(character.Right, character.Bottom)) : default);
        }

        return placed;
    }

    /// <summary>
    /// Where <paramref name="query"/> is on the page as it's shown with <paramref name="rotation"/> added to its own.
    /// Words are matched with the page turned the way its text reads, so a page turned on its side (or text set
    /// sideways) still reads as lines, then the boxes are turned to where they're shown.
    /// </summary>
    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<PdfCharacter> characters, SourcePage source, Rotation rotation, string query)
    {
        ArgumentNullException.ThrowIfNull(characters);
        Rotation reading = FormSpotReader.ReadingTurn(characters, source);
        IReadOnlyList<TextMatch> matches = TextMatcher.Find(OnPage(characters, source, reading), query);
        if (reading == rotation)
        {
            return matches;
        }

        var from = new PageTransform(source, reading);
        var to = new PageTransform(source, rotation);
        PageRect Turn(PageRect box)
        {
            (double x1, double y1) = from.ToPdf(new PagePoint(box.X, box.Y));
            (double x2, double y2) = from.ToPdf(new PagePoint(box.Right, box.Bottom));
            return PageRect.FromCorners(to.ToDisplay(x1, y1), to.ToDisplay(x2, y2));
        }

        return [.. matches.Select(match => new TextMatch([.. match.Boxes.Select(Turn)]))];
    }

    /// <summary>Whether a page has any text to search: a scan or a picture has none until its text is recognised.</summary>
    public static bool HasText(IReadOnlyList<PdfCharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return characters.Any(character => character.Right > character.Left && !char.IsWhiteSpace((char)Math.Min(character.Code, char.MaxValue)));
    }
}
