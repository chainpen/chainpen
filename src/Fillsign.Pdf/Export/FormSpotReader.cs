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

    /// <summary>
    /// The words on the same line just left of <paramref name="area"/>, up to a wide gap (another column): the question
    /// a row of a form's check boxes answers, such as "Contact me by" (UX review 6). Empty when there are none.
    /// </summary>
    /// <param name="area">In the source page's display coordinates (its own /Rotate, no extra turn).</param>
    public static string WordsBefore(PdfiumDocument pdf, int sourceIndex, SourcePage source, PageRect area)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var transform = new PageTransform(source, Rotation.None);
        var line = new List<(int Code, PageRect Box)>();
        foreach (PdfCharacter character in pdf.GetCharacters(sourceIndex))
        {
            PageRect box = PageRect.FromCorners(transform.ToDisplay(character.Left, character.Top), transform.ToDisplay(character.Right, character.Bottom));
            if (box.Centre.Y >= area.Y && box.Centre.Y <= area.Bottom && box.Right <= area.X + 1 && (box.Width > 0 || character.Code == ' '))
            {
                line.Add((character.Code, box));
            }
        }

        // Walk left from the boxes; a gap wider than a few letters is another column, so stop there.
        line.Sort((a, b) => b.Box.X.CompareTo(a.Box.X));
        var words = new System.Text.StringBuilder();
        double? left = null;
        foreach ((int code, PageRect box) in line)
        {
            double size = Math.Max(4, box.Height);
            if (left is { } edge && edge - box.Right > size * 2.5)
            {
                break;
            }

            if (left is { } previous && previous - box.Right > size * 0.2 && code != ' ')
            {
                words.Insert(0, ' ');
            }

            words.Insert(0, char.ConvertFromUtf32(code));
            if (box.Width > 0)
            {
                left = box.X;
            }
        }

        string[] parts = words.ToString().Trim().TrimEnd(':').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.TakeLast(6));
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
