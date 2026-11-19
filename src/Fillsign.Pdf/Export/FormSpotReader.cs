using Fillsign.Core.Gaps;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Export;

/// <summary>Reads a source page's boxes and blanks (FR-8.3) from where its characters and drawn boxes sit.</summary>
public static class FormSpotReader
{
    /// <returns>Spots in the source page's display coordinates (its own /Rotate, no extra turn).</returns>
    public static IReadOnlyList<FormSpot> Read(PdfiumDocument pdf, int sourceIndex, SourcePage source)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // Drawn boxes join the text as ☐ characters, so they're labelled and grouped the same way.
        IReadOnlyList<PdfCharacter> text = pdf.GetCharacters(sourceIndex);
        PdfCharacter[] characters = [.. text, .. pdf.GetDrawnBoxes(sourceIndex)];

        // Lines are found left to right, so look at the page turned the way its text reads, then turn the spots back.
        Rotation reading = ReadingTurn(text, source);
        var transform = new PageTransform(source, reading);
        PageCharacter[] onPage =
        [
            .. characters.Select(character => new PageCharacter(
                character.Code,
                PageRect.FromCorners(transform.ToDisplay(character.Left, character.Top), transform.ToDisplay(character.Right, character.Bottom)))),
        ];
        IReadOnlyList<FormSpot> spots = FormSpots.Find(onPage, transform.DisplaySize.Width);
        return GapFinder.Turn(spots, source, from: reading, to: Rotation.None);
    }

    /// <summary>The extra turn that makes most of the page's text run left to right.</summary>
    private static Rotation ReadingTurn(IReadOnlyList<PdfCharacter> characters, SourcePage source)
    {
        Rotation best = Rotation.None;
        double bestScore = double.MinValue;
        for (int quarterTurns = 0; quarterTurns < 4; quarterTurns++)
        {
            Rotation turn = Rotation.FromQuarterTurns(quarterTurns);
            var transform = new PageTransform(source, turn);
            double score = 0;
            PagePoint? previous = null;
            foreach (PdfCharacter character in characters)
            {
                if (character.Right <= character.Left && character.Top <= character.Bottom)
                {
                    previous = null;
                    continue;
                }

                PagePoint centre = transform.ToDisplay((character.Left + character.Right) / 2, (character.Top + character.Bottom) / 2);
                if (previous is { } last)
                {
                    double dx = centre.X - last.X, dy = centre.Y - last.Y;
                    double size = Math.Max(1, Math.Abs(character.Top - character.Bottom));
                    if (Math.Abs(dx) + Math.Abs(dy) < size * 2)
                    {
                        // Next character to the right on the same line scores; any other step counts against.
                        score += dx - Math.Abs(dy);
                    }
                }

                previous = centre;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = turn;
            }
        }

        return best;
    }
}
