using Chainpen.Core.Finding;
using Chainpen.Core.Gaps;
using Chainpen.Core.Model;

namespace Chainpen.Core.Tests.Finding;

/// <summary>FS-974: finding words on a page the way people expect a reader's search to work.</summary>
public sealed class TextMatcherTests
{
    private const double Glyph = 6;
    private const double Height = 12;

    [Fact]
    public void Case_accents_quotes_and_ligatures_do_not_matter()
    {
        PageCharacter[] page = Line(60, 100, "Patient’s CAFÉ ﬁle – signed");

        Assert.Single(TextMatcher.Find(page, "patient's cafe"));
        Assert.Single(TextMatcher.Find(page, "FILE - SIGNED"));
        Assert.Empty(TextMatcher.Find(page, "patients"));
    }

    [Fact]
    public void Every_match_is_found_and_each_gets_a_box_round_just_its_letters()
    {
        PageCharacter[] page = Line(60, 100, "I consent. Consent form.");

        IReadOnlyList<TextMatch> matches = TextMatcher.Find(page, "consent");

        Assert.Equal(2, matches.Count);
        PageRect first = Assert.Single(matches[0].Boxes);
        Assert.Equal(60 + (2 * Glyph), first.X, 3);
        Assert.Equal(7 * Glyph, first.Width, 3);
        Assert.Equal(60 + (11 * Glyph), matches[1].Bounds.X, 3);
    }

    [Fact]
    public void Words_that_wrap_onto_the_next_line_match_with_a_box_on_each_line()
    {
        // PDFium leaves out the line breaks it infers, so the next line simply carries on in the character list.
        PageCharacter[] page = [.. Line(60, 100, "Please read the patient"), .. Line(60, 116, "consent form carefully.")];

        TextMatch match = Assert.Single(TextMatcher.Find(page, "patient consent"));

        Assert.Equal(2, match.Boxes.Count);
        Assert.Equal(100, match.Boxes[0].Y, 3);
        Assert.Equal(116, match.Boxes[1].Y, 3);
        Assert.Empty(TextMatcher.Find(page, "patientconsent"));
    }

    [Fact]
    public void Extra_spaces_in_the_page_or_the_query_count_as_one()
    {
        PageCharacter[] page = Line(60, 100, "Full   name");

        Assert.Single(TextMatcher.Find(page, "  full name "));
        Assert.Empty(TextMatcher.Find(page, "   "));
    }

    [Fact]
    public void Generated_spaces_with_no_box_still_separate_words()
    {
        // PDFium infers the space between words that are placed apart; it comes with an empty box.
        PageCharacter[] page = [.. Line(60, 100, "Date"), new(' ', default), .. Line(60 + (6 * Glyph), 100, "of birth")];

        TextMatch match = Assert.Single(TextMatcher.Find(page, "date of birth"));
        Assert.Single(match.Boxes);
    }

    [Fact]
    public void A_match_inside_a_line_of_added_text_gives_its_place_in_that_line()
    {
        Assert.Equal([(5, 6), (17, 6)], TextMatcher.FindInLine("I am Jordan, and Jordan is here", "jordan"));
        Assert.Equal([(0, 4)], TextMatcher.FindInLine("Café au lait", "CAFE "));
        Assert.Empty(TextMatcher.FindInLine("Anything", ""));
    }

    /// <summary>A line of text set in a fixed-width font: one box per character.</summary>
    private static PageCharacter[] Line(double x, double y, string text) =>
        [.. text.Select((c, i) => new PageCharacter(c, new PageRect(x + (i * Glyph), y, Glyph, Height)))];
}
