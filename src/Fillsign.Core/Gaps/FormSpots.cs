using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Fillsign.Core.Model;

namespace Fillsign.Core.Gaps;

/// <summary>A character on a page and its box, in display coordinates (points, y down).</summary>
public readonly record struct PageCharacter(int Code, PageRect Box);

/// <summary>What a blank seems to be for, from the words in front of it.</summary>
public enum BlankKind
{
    Text,
    Date,
    Signature,
    Initials,
}

/// <summary>A place on a form meant to be filled: a box to tick, or a blank after a label or along a line.</summary>
public abstract record FormSpot(PageRect Area, string Label);

/// <param name="Group">Boxes on the same line share a group: ticking one answers the others ("Yes ☐ No ☐").</param>
/// <param name="IsTicked">The form came with it ticked (☒, ☑).</param>
public sealed record CheckBoxSpot(PageRect Area, string Label, int Group, bool IsTicked) : FormSpot(Area, Label);

public sealed record BlankSpot(PageRect Area, string Label, BlankKind Kind) : FormSpot(Area, Label);

/// <summary>
/// Finds boxes and blanks in a page's text (FR-8.3), for born-digital forms whose fill-in spots are characters and
/// gaps between them rather than fillable fields: ☐ boxes, "Date:" labels followed by empty space, and runs of
/// underscores. These are hints: the layout can't say for sure what's meant to be filled, so the caller words them so.
/// </summary>
public static class FormSpots
{
    // Characters that are empty boxes, and boxes that are already ticked or crossed (including Wingdings' private-use
    // codes, which Word uses for its check boxes).
    private static readonly ImmutableHashSet<int> s_emptyBoxes = [0x2610, 0x25A1, 0x25A2, 0x274F, 0x2751, 0xF06F, 0xF0A8, 0xF071];
    private static readonly ImmutableHashSet<int> s_tickedBoxes = [0x2611, 0x2612, 0x2BBD, 0x2BBE, 0xF0FE, 0xF0FD, 0xF078];

    // Words that mark something for someone else to fill: the person sending the form shouldn't be pointed at them.
    private static readonly string[] s_notForTheSender = ["assessor", "trainer", "office use", "official use", "staff use", "for office"];

    private const int MinUnderscores = 4;
    private const int MinDots = 6;
    private const int MaxLabelLength = 48;
    private const double MinBlankWidth = 36;

    /// <summary>The page's boxes and blanks, top to bottom.</summary>
    /// <param name="pageWidth">Display width, for blanks that run to the right margin.</param>
    public static IReadOnlyList<FormSpot> Find(IReadOnlyList<PageCharacter> characters, double pageWidth)
    {
        ArgumentNullException.ThrowIfNull(characters);
        List<Line> lines = Lines(Runs(characters));
        var spots = new List<FormSpot>();
        int group = 0;
        foreach (Line line in lines)
        {
            if (IsForSomeoneElse(line.Text))
            {
                continue;
            }

            bool hasBox = false;
            bool labelsBefore = LabelsComeBefore(line);
            foreach (Run run in line.Runs)
            {
                for (int i = 0; i < run.Characters.Count; i++)
                {
                    PageCharacter character = run.Characters[i];
                    bool isEmpty = s_emptyBoxes.Contains(character.Code);
                    if (!isEmpty && !s_tickedBoxes.Contains(character.Code))
                    {
                        continue;
                    }

                    hasBox = true;
                    spots.Add(new CheckBoxSpot(character.Box, BoxLabel(line, run, i, labelsBefore), group, IsTicked: !isEmpty));
                }
            }

            if (hasBox)
            {
                group++;
            }

            spots.AddRange(Blanks(line, pageWidth));
        }

        return JoinStackedChoices(spots);
    }

    /// <summary>
    /// Short options stacked in a column, one box per line ("☐ Satisfactory" above "☐ Not yet satisfactory"), are one
    /// question: ticking one answers it. Long statements stay separate, as each may need its own tick (a consent list).
    /// </summary>
    private static List<FormSpot> JoinStackedChoices(List<FormSpot> spots)
    {
        CheckBoxSpot[] boxes = [.. spots.OfType<CheckBoxSpot>()];
        var groupSizes = boxes.GroupBy(box => box.Group).ToDictionary(group => group.Key, group => group.Count());
        var renamed = new Dictionary<int, int>();
        CheckBoxSpot? previous = null;
        foreach (CheckBoxSpot box in boxes)
        {
            bool stacks = previous is not null
                && groupSizes[box.Group] == 1 && groupSizes[previous.Group] == 1
                && Math.Abs(box.Area.X - previous.Area.X) <= 3
                && box.Area.Y - previous.Area.Bottom < previous.Area.Height * 1.2
                && IsShortOption(box.Label) && IsShortOption(previous.Label);
            renamed[box.Group] = stacks ? renamed[previous!.Group] : box.Group;
            previous = box;
        }

        return [.. spots.Select(spot => spot is CheckBoxSpot box ? box with { Group = renamed[box.Group] } : spot)];
    }

    private static bool IsShortOption(string label) => label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;

    /// <summary>Blanks on one line: underscore or dot lines, and labels ending in ":" with empty space after them.</summary>
    private static IEnumerable<BlankSpot> Blanks(Line line, double pageWidth)
    {
        double rightMargin = pageWidth - 36;
        for (int r = 0; r < line.Runs.Count; r++)
        {
            Run run = line.Runs[r];
            foreach ((int start, int end) in Stretches(run, '_', MinUnderscores).Concat(DotStretches(run, line, r)))
            {
                PageRect underline = Union(run.Characters.Skip(start).Take(end - start).Select(c => c.Box));

                // Writing sits on the line, so the blank reaches a line's height above it.
                var area = new PageRect(underline.X, underline.Bottom - (run.Height * 1.4), underline.Width, run.Height * 1.4);
                string label = LabelBefore(line, run, start);
                yield return new BlankSpot(area, label, KindOf(label));
            }

            int lastBox = run.Characters.FindLastIndex(c => IsBox(c.Code));
            string text = run.Words(lastBox + 1, run.Characters.Count);
            if (!text.EndsWith(':') || text.Length > MaxLabelLength || text.Count(char.IsWhiteSpace) > 4 || !NamesAField(text))
            {
                continue;
            }

            double from = run.Box.Right + 4;
            double to = r + 1 < line.Runs.Count ? line.Runs[r + 1].Box.X - 4 : rightMargin;
            if (to - from < MinBlankWidth)
            {
                continue;
            }

            var blank = new PageRect(from, run.Box.Y - 2, to - from, run.Box.Height + 4);
            string trimmed = text.TrimEnd(':').Trim();
            yield return new BlankSpot(blank, trimmed, KindOf(trimmed));
        }
    }

    /// <summary>Runs of at least <paramref name="minimum"/> of one character, as [start, end) indices.</summary>
    private static IEnumerable<(int Start, int End)> Stretches(Run run, char character, int minimum)
    {
        int start = -1;
        for (int i = 0; i <= run.Characters.Count; i++)
        {
            bool matches = i < run.Characters.Count && run.Characters[i].Code == character;
            if (matches && start < 0)
            {
                start = i;
            }
            else if (!matches && start >= 0)
            {
                if (i - start >= minimum)
                {
                    yield return (start, i);
                }

                start = -1;
            }
        }
    }

    /// <summary>Dot lines to write on, but not a table of contents' dot leaders, which end in a page number.</summary>
    private static IEnumerable<(int Start, int End)> DotStretches(Run run, Line line, int runIndex)
    {
        foreach ((int start, int end) in Stretches(run, '.', MinDots))
        {
            string after = string.Concat(run.Characters.Skip(end).Select(c => char.ConvertFromUtf32(c.Code)))
                + string.Concat(line.Runs.Skip(runIndex + 1).Select(other => other.Text));
            if (after.Trim().Length > 0 && after.Trim().All(char.IsDigit))
            {
                continue;
            }

            yield return (start, end);
        }
    }

    /// <summary>
    /// The words that go with a box: up to the next box after it, or the last few words before it when nothing
    /// follows ("Yes ☐").
    /// </summary>
    private static string BoxLabel(Line line, Run run, int index, bool labelsBefore)
    {
        string before = WordsBeforeBox(line, run, index);
        string after = WordsAfterBox(line, run, index);
        string label = labelsBefore ? (before.Length > 0 ? before : after) : (after.Length > 0 ? after : before);
        return Shorten(label.TrimEnd(':'));
    }

    /// <summary>The words between this box and the next one (or the line's end).</summary>
    private static string WordsAfterBox(Line line, Run run, int index)
    {
        int nextBox = run.Characters.FindIndex(index + 1, c => IsBox(c.Code));
        string after = run.Words(index + 1, nextBox < 0 ? run.Characters.Count : nextBox);
        if (after.Length == 0 && nextBox < 0 && line.Runs.IndexOf(run) is var at and >= 0 && at + 1 < line.Runs.Count)
        {
            Run next = line.Runs[at + 1];
            int firstBox = next.Characters.FindIndex(c => IsBox(c.Code));
            after = next.Words(0, firstBox < 0 ? next.Characters.Count : firstBox);
        }

        return after;
    }

    /// <summary>The last few words between the previous box (or the line's start) and this box.</summary>
    private static string WordsBeforeBox(Line line, Run run, int index)
    {
        int previousBox = index == 0 ? -1 : run.Characters.FindLastIndex(index - 1, c => IsBox(c.Code));
        string before = run.Words(previousBox + 1, index);
        if (before.Length == 0 && previousBox < 0 && line.Runs.IndexOf(run) is var at and > 0)
        {
            Run previous = line.Runs[at - 1];
            int lastBox = previous.Characters.FindLastIndex(c => IsBox(c.Code));
            before = previous.Words(lastBox + 1, previous.Characters.Count);
        }

        return LastWords(before, 6);
    }

    /// <summary>
    /// Whether a line reads "Yes ☐ No ☐" (each label before its box) rather than "☐ Yes ☐ No": words come before the
    /// first box, and after the last one there's nothing or only another label ("… ☐ Date:").
    /// </summary>
    private static bool LabelsComeBefore(Line line)
    {
        string text = line.Text;
        int first = text.EnumerateRunes().Select((rune, i) => (rune, i)).FirstOrDefault(pair => IsBox(pair.rune.Value), (default, -1)).Item2;
        if (first < 0)
        {
            return false;
        }

        int[] boxes = [.. text.EnumerateRunes().Select((rune, i) => IsBox(rune.Value) ? i : -1).Where(i => i >= 0)];
        Rune[] runes = [.. text.EnumerateRunes()];
        string beforeFirst = string.Concat(runes.Take(boxes[0]).Select(r => r.ToString())).Trim();
        string afterLast = string.Concat(runes.Skip(boxes[^1] + 1).Select(r => r.ToString())).Trim();
        return beforeFirst.Length > 0 && (afterLast.Length == 0 || afterLast.EndsWith(':'));
    }

    private static string LabelBefore(Line line, Run run, int start)
    {
        int previousBox = start == 0 ? -1 : run.Characters.FindLastIndex(start - 1, c => IsBox(c.Code));
        string before = run.Words(previousBox + 1, start);
        if (before.Length == 0 && line.Runs.IndexOf(run) is var at and > 0)
        {
            Run previous = line.Runs[at - 1];
            before = previous.Words(0, previous.Characters.Count);
        }

        return Shorten(LastWords(before.TrimEnd(':').Trim(), 6));
    }

    private static bool IsBox(int code) => s_emptyBoxes.Contains(code) || s_tickedBoxes.Contains(code);

    private static string LastWords(string text, int count)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Skip(Math.Max(0, words.Length - count)));
    }

    private static string Shorten(string label)
    {
        string single = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 60 ? single : string.Concat(single.AsSpan(0, 57), "…");
    }

    private static BlankKind KindOf(string label)
    {
        string lower = label.ToLower(CultureInfo.InvariantCulture);
        return lower.Contains("initial", StringComparison.Ordinal) ? BlankKind.Initials
            : lower.Contains("sign", StringComparison.Ordinal) ? BlankKind.Signature
            : lower.Contains("date", StringComparison.Ordinal) || lower.Contains("dob", StringComparison.Ordinal) ? BlankKind.Date
            : BlankKind.Text;
    }

    /// <summary>
    /// Whether a label names something people write in. A colon alone also ends headings and sentences ("Residential
    /// feedback:"), so only labels like these count.
    /// </summary>
    private static bool NamesAField(string label)
    {
        // Whole words, or the start of one for longer words ("Signature", "Initials"), so "id" isn't found in "provided".
        string[] words = label.ToLower(CultureInfo.InvariantCulture).Split([' ', ':', '/', '(', ')', '-', ','], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => s_fieldWords.Any(field => word == field || (field.Length >= 4 && word.StartsWith(field, StringComparison.Ordinal))));
    }

    private static readonly string[] s_fieldWords =
    [
        "name", "date", "signature", "signed", "sign", "initials", "initial", "number", "no", "no.", "address", "phone", "mobile", "tel", "email", "e-mail",
        "birth", "dob", "id", "campus", "postcode", "contact", "relationship", "occupation", "school", "class", "course",
        "time", "location", "age", "gender", "title", "position", "organisation", "organization", "company", "suburb",
        "state", "country", "witness", "employer",
    ];

    private static bool IsForSomeoneElse(string text)
    {
        string lower = text.ToLower(CultureInfo.InvariantCulture);
        return s_notForTheSender.Any(words => lower.Contains(words, StringComparison.Ordinal));
    }

    /// <summary>
    /// Characters in text order split into runs: a new run starts on a new line or after a gap wider than a couple of
    /// spaces, so a table's cells and a label's empty space separate runs.
    /// </summary>
    private static List<Run> Runs(IReadOnlyList<PageCharacter> characters)
    {
        var runs = new List<Run>();
        Run? current = null;
        foreach (PageCharacter character in characters)
        {
            bool isSpace = character.Code <= char.MaxValue && char.IsWhiteSpace((char)character.Code);
            if (isSpace || character.Box.Height <= 0)
            {
                if (isSpace && current is not null)
                {
                    current.SpaceSeen = true;
                }

                continue;
            }

            if (current is null || !current.Continues(character))
            {
                current = new Run();
                runs.Add(current);
            }

            current.Add(character);
        }

        return runs;
    }

    /// <summary>Runs that share a line (their middles at the same height), left to right, top to bottom.</summary>
    private static List<Line> Lines(List<Run> runs)
    {
        var lines = new List<Line>();
        foreach (Run run in runs.OrderBy(run => run.Box.Centre.Y))
        {
            Line? line = lines.LastOrDefault();
            if (line is null || Math.Abs(line.Middle - run.Box.Centre.Y) > Math.Min(line.Height, run.Height) * 0.5)
            {
                line = new Line();
                lines.Add(line);
            }

            line.Runs.Add(run);
        }

        foreach (Line line in lines)
        {
            line.Runs.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
        }

        return lines;
    }

    private static PageRect Union(IEnumerable<PageRect> boxes)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (PageRect box in boxes)
        {
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }

        return new PageRect(left, top, right - left, bottom - top);
    }

    private sealed class Run
    {
        private readonly StringBuilder _text = new();

        public List<PageCharacter> Characters { get; } = [];

        public PageRect Box { get; private set; }

        public double Height => Box.Height;

        public bool SpaceSeen { get; set; }

        public string Text => _text.ToString();

        /// <summary>Whether the character carries on this run: same line, and no wider a gap than a few spaces.</summary>
        public bool Continues(PageCharacter next)
        {
            PageCharacter last = Characters[^1];
            double height = Math.Max(last.Box.Height, next.Box.Height);
            bool sameLine = Math.Abs(last.Box.Centre.Y - next.Box.Centre.Y) < height * 0.4;
            double gap = next.Box.X - last.Box.Right;
            return sameLine && gap > -height * 0.5 && gap < height * 1.2;
        }

        /// <summary>Whether a space goes before each character, from the PDF's spaces or a word-sized gap.</summary>
        public List<bool> SpaceBefore { get; } = [];

        /// <summary>The words from character <paramref name="from"/> up to <paramref name="to"/>, boxes and underscores left out.</summary>
        public string Words(int from, int to)
        {
            var text = new StringBuilder();
            for (int i = Math.Max(0, from); i < Math.Min(to, Characters.Count); i++)
            {
                int code = Characters[i].Code;
                if (s_emptyBoxes.Contains(code) || s_tickedBoxes.Contains(code) || code == '_')
                {
                    text.Append(' ');
                    continue;
                }

                if (SpaceBefore[i] && i > from)
                {
                    text.Append(' ');
                }

                text.Append(char.ConvertFromUtf32(code));
            }

            return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public void Add(PageCharacter character)
        {
            bool space = Characters.Count > 0 && (SpaceSeen || character.Box.X - Characters[^1].Box.Right > character.Box.Height * 0.15);
            if (space)
            {
                _text.Append(' ');
            }

            SpaceSeen = false;
            SpaceBefore.Add(space);
            Characters.Add(character);
            _text.Append(char.ConvertFromUtf32(character.Code));
            Box = Characters.Count == 1 ? character.Box : Union([Box, character.Box]);
        }
    }

    private sealed class Line
    {
        public List<Run> Runs { get; } = [];

        public double Middle => Runs.Average(run => run.Box.Centre.Y);

        public double Height => Runs.Max(run => run.Height);

        public string Text => string.Join(' ', Runs.Select(run => run.Text));
    }
}
