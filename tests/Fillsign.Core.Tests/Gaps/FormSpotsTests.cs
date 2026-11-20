using Fillsign.Core.Gaps;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Gaps;

/// <summary>FS-609: finding boxes and blanks in a page's text.</summary>
public sealed class FormSpotsTests
{
    private const double PageWidth = 600;

    [Fact]
    public void Boxes_are_labelled_by_the_words_after_them_and_each_line_is_its_own_question()
    {
        IReadOnlyList<FormSpot> spots = Find(
            Line(60, 100, "☐ I consent to the treatment described to me."),
            Line(60, 140, "☐ I would like a copy of this form."));

        CheckBoxSpot[] boxes = [.. spots.OfType<CheckBoxSpot>()];
        Assert.Equal(["I consent to the treatment described to me.", "I would like a copy of this form."], boxes.Select(box => box.Label));
        Assert.NotEqual(boxes[0].Group, boxes[1].Group);
        Assert.All(boxes, box => Assert.False(box.IsTicked));
    }

    [Fact]
    public void Labels_before_their_boxes_are_read_that_way_and_a_ticked_box_is_known()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Result: Satisfactory ☒ Not satisfactory ☐"));
        CheckBoxSpot[] boxes = [.. spots.OfType<CheckBoxSpot>()];
        Assert.Equal(["Result: Satisfactory", "Not satisfactory"], boxes.Select(box => box.Label));
        Assert.Equal(boxes[0].Group, boxes[1].Group);
        Assert.True(boxes[0].IsTicked);
    }

    [Fact]
    public void Short_options_stacked_in_a_column_are_one_question()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "☐ Satisfactory"), Line(60, 114, "☐ Not yet satisfactory"));
        CheckBoxSpot[] boxes = [.. spots.OfType<CheckBoxSpot>()];
        Assert.Equal(boxes[0].Group, boxes[1].Group);
    }

    [Fact]
    public void A_label_with_empty_space_after_it_is_a_blank_and_its_kind_comes_from_its_words()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Student Name:"), Line(60, 140, "Date of birth:"), Line(60, 180, "Signature:"));
        BlankSpot[] blanks = [.. spots.OfType<BlankSpot>()];
        Assert.Equal(["Student Name", "Date of birth", "Signature"], blanks.Select(blank => blank.Label));
        Assert.Equal([BlankKind.Text, BlankKind.Date, BlankKind.Signature], blanks.Select(blank => blank.Kind));
        Assert.True(blanks[0].Area.X > 60 + (13 * 6), "the blank starts after the label");
        Assert.True(blanks[0].Area.Right > 500, "and runs to the margin");
    }

    [Fact]
    public void Headings_and_sentences_ending_in_a_colon_are_not_blanks()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Residential feedback:"), Line(60, 140, "Please read the following before you start:"));
        Assert.Empty(spots);
    }

    [Fact]
    public void A_label_followed_closely_by_its_answer_is_not_a_blank()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Name: Jordan Lee"));
        Assert.Empty(spots);
    }

    [Fact]
    public void Underscore_lines_are_blanks_but_contents_dot_leaders_are_not()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Witness ____________"), Line(60, 140, "Introduction ........................ 5"));
        BlankSpot blank = Assert.Single(spots.OfType<BlankSpot>());
        Assert.Equal("Witness", blank.Label);
        Assert.True(blank.Area.Bottom >= 100 && blank.Area.Y < 100, "the blank sits on the line, reaching above it");
    }

    [Fact]
    public void Lines_for_the_assessor_are_left_out()
    {
        IReadOnlyList<FormSpot> spots = Find(Line(60, 100, "Assessor name:"), Line(60, 140, "Assessor ☐ Satisfactory"));
        Assert.Empty(spots);
    }

    [Fact]
    public void Codes_that_are_not_characters_are_skipped_rather_than_crashing()
    {
        // A damaged font map can report lone surrogates or values past Unicode's end.
        PageCharacter[] line = [.. Line(60, 100, "Name:"), new(0xD800, new PageRect(400, 100, 6, 12)), new(0x110000, new PageRect(420, 100, 6, 12))];
        BlankSpot blank = Assert.Single(Find(line).OfType<BlankSpot>());
        Assert.Equal("Name", blank.Label);
    }

    private static IReadOnlyList<FormSpot> Find(params PageCharacter[][] lines) => FormSpots.Find([.. lines.SelectMany(line => line)], PageWidth);

    /// <summary>Characters 6 pt wide and 12 pt tall from (x, top), spaces included, as a PDF's text gives them.</summary>
    private static PageCharacter[] Line(double x, double top, string text) =>
        [.. text.EnumerateRunes().Select((rune, i) => new PageCharacter(rune.Value, new PageRect(x + (i * 6), top, 6, 12)))];
}
