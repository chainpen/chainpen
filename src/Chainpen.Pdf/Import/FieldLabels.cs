using System.Text;
using Chainpen.Pdf.Localization;
using Chainpen.Pdf.Pdfium;

namespace Chainpen.Pdf.Import;

/// <summary>
/// Words for form fields that don't describe themselves (FS-950). Many real forms (the IRS's among them) give fields
/// only machine names such as <c>topmostSubform[0].Page1[0].f1_01[0]</c>, which make poor gap hints and screen-reader
/// names. The words printed just before a field on its line, or else just above it, usually say what it asks.
/// </summary>
public static class FieldLabels
{
    private const double MaxDistanceBefore = 220;
    private const double MaxDistanceAbove = 18;
    private const int MaxLength = 60;

    /// <summary>
    /// The fields with a label added where they have none: from the form's XFA descriptions when it has them
    /// (<paramref name="xfaDescriptions"/>, by full field name), else from the words printed beside them.
    /// </summary>
    public static IReadOnlyList<FormField> FromPage(
        IReadOnlyList<FormField> fields,
        IReadOnlyList<PdfCharacter> characters,
        IReadOnlyDictionary<string, string>? xfaDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(characters);
        if (xfaDescriptions is { Count: > 0 })
        {
            fields = [.. fields.Select(field => field.Label.Length == 0 && xfaDescriptions.TryGetValue(field.Name, out string? description)
                ? field with { Label = Shorten(Tidy(description)) }
                : field)];
        }

        if (characters.Count == 0 || fields.All(field => field.Label.Length > 0))
        {
            return fields;
        }

        // A radio group's words are its question, printed before its first button ("Membership ◯ Individual ◯ Family");
        // each button's own words are just its option.
        var questions = fields
            .Where(field => field.Kind == FormFieldKind.Radio && field.Label.Length == 0)
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => Find(group.OrderByDescending(field => Math.Round(field.Rect.Top)).ThenBy(field => field.Rect.Left).First(), characters, isQuestion: true),
                StringComparer.Ordinal);

        FormField[] labelled = [.. fields.Select(field => field.Label.Length > 0 ? field
            : field.Kind == FormFieldKind.Radio ? field with { Label = questions[field.Name] }
            : field with { Label = Find(field, characters, isQuestion: false) })];
        return NumberPieces(labelled, fields);
    }

    /// <summary>
    /// Pieces of one question in a row (a date as day, month, year boxes) each pick up the words before them, which
    /// grow along the row ("Deceased", "Deceased MM /", "Deceased MM / DD /"). Those become the first piece's words
    /// with a part number ("Deceased, part 2"). Only labels found here are changed, never a form's own descriptions.
    /// </summary>
    private static FormField[] NumberPieces(FormField[] labelled, IReadOnlyList<FormField> original)
    {
        var parts = new Dictionary<string, int>(StringComparer.Ordinal);
        var roots = new Dictionary<int, string>();
        for (int i = 0; i < labelled.Length; i++)
        {
            FormField field = labelled[i];
            if (original[i].Label.Length > 0 || field.Label.Length == 0 || field.Kind != FormFieldKind.Text)
            {
                continue;
            }

            double middle = (field.Rect.Bottom + field.Rect.Top) / 2;
            int earlier = labelled.Take(i).Select((other, index) => (Other: other, Index: index))
                .Where(pair => pair.Other.Kind == FormFieldKind.Text && pair.Other.Label.Length > 0
                    && Math.Abs(((pair.Other.Rect.Bottom + pair.Other.Rect.Top) / 2) - middle) <= 4 && pair.Other.Rect.Right <= field.Rect.Left + 2)
                .OrderByDescending(pair => pair.Other.Rect.Left)
                .Select(pair => pair.Index)
                .DefaultIfEmpty(-1)
                .First();
            if (earlier < 0)
            {
                continue;
            }

            // The first piece's own words, not its numbered label, whatever language "part" is written in.
            string root = roots.GetValueOrDefault(earlier, labelled[earlier].Label);
            if (field.Label == root || field.Label.StartsWith(root + " ", StringComparison.Ordinal))
            {
                int part = parts.GetValueOrDefault(root, 1) + 1;
                parts[root] = part;
                roots[i] = root;
                labelled[i] = field with { Label = Text.Format(Strings.Field_LabelPart, root, part) };
            }
        }

        return labelled;
    }

    /// <summary>
    /// XFA descriptions are often written to be read aloud page by page ("Page 1. Step 1: …."): the page and the
    /// closing full stop say nothing a label needs.
    /// </summary>
    private static string Tidy(string description)
    {
        string tidy = System.Text.RegularExpressions.Regex.Replace(description, @"^Page \d+\.\s*", string.Empty);
        return tidy.EndsWith('.') && !tidy.EndsWith("..", StringComparison.Ordinal) ? tidy[..^1] : tidy;
    }

    private static string Shorten(string words)
    {
        if (words.Length <= MaxLength)
        {
            return words;
        }

        int cut = words.LastIndexOf(' ', MaxLength);
        return (cut > 20 ? words[..cut] : words[..MaxLength]) + "…";
    }

    private static string Find(FormField field, IReadOnlyList<PdfCharacter> characters, bool isQuestion)
    {
        (double left, double bottom, double right, double top) = field.Rect;
        bool IsLevel(PdfCharacter c) => (c.Top + c.Bottom) / 2 >= bottom - 1 && (c.Top + c.Bottom) / 2 <= top + 1;

        // A box or button's words usually follow it ("☐ Individual"); a text field's come before it or above it.
        string After() => Usable(NearestWords(TopLine(characters.Where(c => IsLevel(c) && c.Left >= right - 2 && c.Left <= right + MaxDistanceBefore)), fromEnd: false));
        string Before() => Usable(NearestWords(TopLine(characters.Where(c => IsLevel(c) && c.Right <= left + 2 && c.Right >= left - (2 * MaxDistanceBefore))), fromEnd: true));
        string Above(bool captionOnly)
        {
            PdfCharacter[] above = [.. characters
                .Where(c => c.Bottom >= top - 2 && c.Bottom <= top + MaxDistanceAbove && c.Right > left - 2 && c.Left < right)
                .OrderByDescending(c => Math.Round(c.Bottom))];
            if (above.Length == 0)
            {
                return string.Empty;
            }

            // The whole line, so words that run past the field's edges aren't cut.
            double line = Math.Round(above[0].Bottom);
            PdfCharacter[] row = [.. characters.Where(c => Math.Abs(Math.Round(c.Bottom) - line) <= 2).OrderBy(c => c.Left)];
            int start = Array.FindIndex(row, c => c.Right > left - 2);
            while (start > 0 && row[start].Left - row[start - 1].Right < Math.Max(1, (row[start].Top - row[start].Bottom) * 0.25))
            {
                start--;
            }

            // A caption sits over the field's start; text that only overhangs it belongs to something else.
            return captionOnly && row[start].Left > left + 12 ? string.Empty : Usable(NearestWords(row[start..], fromEnd: false));
        }

        return isQuestion
            ? FirstOf(Before(), Above(captionOnly: true))
            : field.Kind is FormFieldKind.CheckBox
            ? FirstOf(After(), Before())
            : FirstOf(Before(), Above(captionOnly: true), Above(captionOnly: false));
    }

    /// <summary>
    /// Beside a tall field (a list, several lines), more than one printed line can be level with it; its caption is
    /// the top one ("How did you hear?" over "(choose any)").
    /// </summary>
    private static PdfCharacter[] TopLine(IEnumerable<PdfCharacter> level)
    {
        PdfCharacter[] all = [.. level];
        if (all.Length == 0)
        {
            return all;
        }

        double top = all.Max(c => Math.Round(c.Bottom));
        return [.. all.Where(c => Math.Abs(Math.Round(c.Bottom) - top) <= 2).OrderBy(c => c.Left)];
    }

    private static string FirstOf(params string[] labels) => labels.FirstOrDefault(label => label.Length > 0) ?? string.Empty;

    /// <summary>Words that say something: dot leaders and currency signs trimmed, and at least one real word.</summary>
    private static string Usable(string words)
    {
        string cleaned = System.Text.RegularExpressions.Regex.Replace(words, @"(\s*\.){3,}", " ").Trim(' ', ':', '_', '.', '…', '$', ',');
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        bool hasWord = cleaned.Split(' ').Any(word => word.Count(char.IsLetter) >= 3);
        return hasWord ? cleaned : string.Empty;
    }

    /// <summary>
    /// The run of words nearest the field: from the end of the line when it's before the field, from the start when
    /// it's above. A gap wider than three spaces ends the run, so a neighbouring column's words aren't taken.
    /// </summary>
    private static string NearestWords(PdfCharacter[] line, bool fromEnd)
    {
        if (line.Length == 0)
        {
            return string.Empty;
        }

        var run = new List<PdfCharacter>();
        IEnumerable<PdfCharacter> order = fromEnd ? line.Reverse() : line;
        PdfCharacter? previous = null;
        foreach (PdfCharacter character in order)
        {
            if (previous is { } last)
            {
                double gap = fromEnd ? last.Left - character.Right : character.Left - last.Right;
                double size = Math.Max(4, last.Top - last.Bottom);
                if (gap > size * 1.5)
                {
                    break;
                }
            }

            run.Add(character);
            previous = character;
        }

        if (fromEnd)
        {
            run.Reverse();
        }

        var text = new StringBuilder();
        PdfCharacter? before = null;
        foreach (PdfCharacter character in run)
        {
            if (before is { } b && character.Left - b.Right > Math.Max(1, (b.Top - b.Bottom) * 0.2) && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }

            // A damaged font map can give codes that aren't characters at all; they can't be part of a label.
            if (character.Code > 0 && Rune.IsValid(character.Code) && !char.IsControl((char)Math.Min(character.Code, char.MaxValue)))
            {
                text.Append(char.ConvertFromUtf32(character.Code));
            }

            before = character;
        }

        string words = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ':', '_', '.', '…');
        if (words.Length > MaxLength)
        {
            int cut = words.LastIndexOf(' ', MaxLength);
            words = (cut > 20 ? words[..cut] : words[..MaxLength]) + "…";
        }

        return words;
    }
}
