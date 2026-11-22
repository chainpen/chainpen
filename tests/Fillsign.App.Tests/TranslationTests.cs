using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fillsign.App.Tests;

/// <summary>
/// Every translation has every English string, with the same placeholders, "Mod" shortcut tokens and tool letters,
/// so nothing goes missing or breaks when the text is filled in at run time.
/// </summary>
public sealed partial class TranslationTests
{
    public static TheoryData<string, string> Translations => new()
    {
        { "src/Fillsign.App/Localization/Strings.resx", "de" },
        { "src/Fillsign.App/Localization/Strings.resx", "ja" },
        { "src/Fillsign.Pdf/Localization/Strings.resx", "de" },
        { "src/Fillsign.Pdf/Localization/Strings.resx", "ja" },
    };

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_has_every_string_with_the_same_placeholders_and_tokens(string english, string language)
    {
        Dictionary<string, string> source = Load(english);
        Dictionary<string, string> translated = Load(english.Replace(".resx", $".{language}.resx", StringComparison.Ordinal));
        var problems = new List<string>();
        foreach ((string key, string text) in source)
        {
            if (!translated.TryGetValue(key, out string? other))
            {
                problems.Add($"{key}: missing");
                continue;
            }

            if (other.Trim().Length == 0 && text.Trim().Length > 0)
            {
                problems.Add($"{key}: empty");
            }

            if (!Sorted(Placeholder().Matches(text)).SequenceEqual(Sorted(Placeholder().Matches(other))))
            {
                problems.Add($"{key}: placeholders differ");
            }

            if (ModToken().Count(text) != ModToken().Count(other))
            {
                problems.Add($"{key}: Mod shortcut tokens differ");
            }

            if (ToolLetter().Match(text) is { Success: true } letter && !other.EndsWith(letter.Value, StringComparison.Ordinal))
            {
                problems.Add($"{key}: should end with the tool letter {letter.Value.Trim()}");
            }
        }

        problems.AddRange(translated.Keys.Except(source.Keys).Select(key => $"{key}: not in the English"));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static Dictionary<string, string> Load(string relative) =>
        XDocument.Load(Path.Combine(TestFiles.RepoRoot, relative)).Root!.Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);

    private static IEnumerable<string> Sorted(MatchCollection matches) => matches.Select(match => match.Value).Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    // "Mod" as its own token: Japanese writes it straight after other letters ("またはMod").
    [GeneratedRegex("(?<![A-Za-z])Mod(?![A-Za-z])")]
    private static partial Regex ModToken();

    [GeneratedRegex(@" \([A-Z]\)$")]
    private static partial Regex ToolLetter();
}
