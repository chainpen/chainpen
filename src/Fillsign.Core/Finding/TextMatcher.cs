using System.Globalization;
using System.Text;
using Fillsign.Core.Gaps;
using Fillsign.Core.Model;

namespace Fillsign.Core.Finding;

/// <summary>One place the words were found on a page: a box for each line the match runs across.</summary>
public sealed record TextMatch(IReadOnlyList<PageRect> Boxes)
{
    /// <summary>Everything the match covers, for scrolling it into view.</summary>
    public PageRect Bounds => Boxes.Skip(1).Aggregate(Boxes[0], Union);

    private static PageRect Union(PageRect a, PageRect b) =>
        PageRect.FromCorners(new PagePoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new PagePoint(Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
}

/// <summary>
/// Find (FS-974): where some words appear on a page, the way people expect a reader's search to work. Case and accents
/// don't matter ("cafe" finds "Café"), nor do curly quotes, dashes or ligatures, and runs of spaces count as one. Words
/// that wrap onto the next line still match, as if the line break were a space.
/// </summary>
public static class TextMatcher
{
    /// <summary>The words as they're compared: folded, with spaces collapsed and trimmed.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            AppendFolded(folded, rune.Value);
        }

        return CollapseSpaces(folded.ToString());
    }

    /// <summary>Every match of <paramref name="query"/> among a page's characters, in the page's reading order.</summary>
    /// <param name="characters">The page's characters with their boxes, in text order. Spaces may have empty boxes.</param>
    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<PageCharacter> characters, string query)
    {
        ArgumentNullException.ThrowIfNull(characters);
        string wanted = Fold(query);
        if (wanted.Length == 0 || characters.Count == 0)
        {
            return [];
        }

        // The page's text, folded the same way, with each folded character's source character remembered.
        var text = new StringBuilder(characters.Count);
        var owners = new List<int>(characters.Count);
        PageRect? previous = null;
        for (int i = 0; i < characters.Count; i++)
        {
            PageCharacter character = characters[i];
            bool hasBox = character.Box.Width > 0 && character.Box.Height > 0;
            if (hasBox && previous is { } before && StartsNewLine(before, character.Box))
            {
                AppendSpace(text, owners, i);
            }

            int length = text.Length;
            AppendFolded(text, character.Code);
            for (int added = length; added < text.Length; added++)
            {
                if (text[added] == ' ')
                {
                    // Collapse spaces as the query was collapsed.
                    text.Remove(added, 1);
                    AppendSpace(text, owners, i);
                    break;
                }

                owners.Add(i);
            }

            if (hasBox)
            {
                previous = character.Box;
            }
        }

        string page = text.ToString();
        var matches = new List<TextMatch>();
        for (int at = page.IndexOf(wanted, StringComparison.Ordinal); at >= 0; at = page.IndexOf(wanted, at + wanted.Length, StringComparison.Ordinal))
        {
            List<PageRect> boxes = LineBoxes(characters, owners, at, wanted.Length);
            if (boxes.Count > 0)
            {
                matches.Add(new TextMatch(boxes));
            }
        }

        return matches;
    }

    /// <summary>Where <paramref name="query"/> appears in one line of plain text: (start, length) in the line's own characters.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindInLine(string line, string query)
    {
        ArgumentNullException.ThrowIfNull(line);
        string wanted = Fold(query);
        if (wanted.Length == 0)
        {
            return [];
        }

        var text = new StringBuilder(line.Length);
        var owners = new List<int>(line.Length);
        for (int i = 0; i < line.Length;)
        {
            Rune rune = Rune.GetRuneAt(line, i);
            int length = text.Length;
            AppendFolded(text, rune.Value);
            for (int added = length; added < text.Length; added++)
            {
                if (text[added] == ' ')
                {
                    text.Remove(added, 1);
                    AppendSpace(text, owners, i);
                    break;
                }

                owners.Add(i);
            }

            i += rune.Utf16SequenceLength;
        }

        string folded = text.ToString();
        var found = new List<(int, int)>();
        for (int at = folded.IndexOf(wanted, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(wanted, at + wanted.Length, StringComparison.Ordinal))
        {
            int start = owners[at];
            int last = owners[at + wanted.Length - 1];
            int end = last + Rune.GetRuneAt(line, last).Utf16SequenceLength;
            found.Add((start, end - start));
        }

        return found;
    }

    /// <summary>
    /// A character on a different line from the one before: its middle is outside that one's height, or it starts well
    /// to the left (the next line of a column).
    /// </summary>
    private static bool StartsNewLine(PageRect before, PageRect next)
    {
        double middle = next.Centre.Y;
        bool isOtherRow = middle < before.Y || middle > before.Bottom;
        bool isBackwards = next.X < before.X - Math.Max(before.Height, next.Height);
        return isOtherRow || isBackwards;
    }

    /// <summary>The matched characters' boxes, joined into one box per line.</summary>
    private static List<PageRect> LineBoxes(IReadOnlyList<PageCharacter> characters, List<int> owners, int start, int length)
    {
        var lines = new List<PageRect>();
        int last = -1;
        for (int i = start; i < start + length; i++)
        {
            int owner = owners[i];
            if (owner == last)
            {
                continue;
            }

            last = owner;
            PageRect box = characters[owner].Box;
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            if (lines.Count > 0 && !StartsNewLine(lines[^1], box))
            {
                PageRect line = lines[^1];
                lines[^1] = PageRect.FromCorners(
                    new PagePoint(Math.Min(line.X, box.X), Math.Min(line.Y, box.Y)),
                    new PagePoint(Math.Max(line.Right, box.Right), Math.Max(line.Bottom, box.Bottom)));
            }
            else
            {
                lines.Add(box);
            }
        }

        return lines;
    }

    private static void AppendSpace(StringBuilder text, List<int> owners, int owner)
    {
        if (text.Length > 0 && text[^1] != ' ')
        {
            text.Append(' ');
            owners.Add(owner);
        }
    }

    /// <summary>One character as it's compared: lower case, without accents, ligatures spelt out, quotes and dashes plain.</summary>
    private static void AppendFolded(StringBuilder text, int code)
    {
        switch (code)
        {
            case 0x00AD or 0xFFFE or 0xFFFF or 0x200B or 0x200C or 0x200D or 0xFEFF:
                // Soft hyphens, PDFium's marker for a hyphen at a line end, and zero-width characters: nothing to match.
                return;
            case 0x2018 or 0x2019 or 0x201A or 0x2032:
                text.Append('\'');
                return;
            case 0x201C or 0x201D or 0x201E or 0x2033:
                text.Append('"');
                return;
            case 0x2010 or 0x2011 or 0x2012 or 0x2013 or 0x2014 or 0x2212:
                text.Append('-');
                return;
        }

        if (!Rune.IsValid(code))
        {
            return;
        }

        var rune = new Rune(code);
        if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
        {
            text.Append(' ');
            return;
        }

        foreach (char c in rune.ToString().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            text.Append(char.ToLowerInvariant(c));
        }
    }

    private static string CollapseSpaces(string text)
    {
        var collapsed = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == ' ' && (collapsed.Length == 0 || collapsed[^1] == ' '))
            {
                continue;
            }

            collapsed.Append(c);
        }

        return collapsed.ToString().TrimEnd(' ');
    }
}
