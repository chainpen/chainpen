using System.Collections.Immutable;
using Fillsign.Core.Editing;
using Fillsign.Core.Geometry;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests.Editing;

/// <summary>FS-957: initials (or anything selected) copied to every other page in one step.</summary>
public sealed class EveryPageTests
{
    private static readonly ITextMeasurer s_measurer = ApproximateTextMeasurer.Instance;

    [Fact]
    public void Initials_in_a_corner_keep_their_distance_from_that_corner_on_pages_of_another_size()
    {
        DocumentState document = DocumentState.ForSourcePages(3);
        PageId first = document.Pages[0].Id, letter = document.Pages[1].Id, small = document.Pages[2].Id;
        PageSize SizeOf(PageId page) => page == letter ? new PageSize(612, 792) : page == small ? new PageSize(300, 400) : TestData.A4;

        // Initials 60 × 20, 30 pt in from the right edge and 40 pt up from the bottom of A4.
        var initials = new SignatureItem(ItemId.New(), first, 595 - 30 - 60, 842 - 40 - 20, SignatureKind.Initials, TestData.Png(), 60, 20);
        document = document.WithItems([initials]);

        DocumentState next = ItemEdits.CopyToEveryPage(document, TestData.Set(initials.Id), SizeOf, s_measurer, out ImmutableArray<ItemId> added);

        Assert.Equal(2, added.Length);
        Assert.Equal(3, next.Items.Count);
        var onLetter = (SignatureItem)Assert.Single(next.ItemsOn(letter));
        Assert.Equal((612 - 30 - 60, 792 - 40 - 20), (onLetter.X, onLetter.Y));
        Assert.Equal(SignatureKind.Initials, onLetter.Kind);
        var onSmall = (SignatureItem)Assert.Single(next.ItemsOn(small));
        Assert.Equal((300 - 30 - 60, 400 - 40 - 20), (onSmall.X, onSmall.Y));
        Assert.All(added, id => Assert.NotEqual(initials.Id, id));
    }

    [Fact]
    public void Copies_stay_on_pages_too_small_to_keep_the_distance()
    {
        DocumentState document = DocumentState.ForSourcePages(2);
        PageId first = document.Pages[0].Id, tiny = document.Pages[1].Id;
        PageSize SizeOf(PageId page) => page == tiny ? new PageSize(100, 100) : TestData.A4;
        ShapeItem box = TestData.Rectangle(first, x: 10, y: 20, w: 80, h: 40);
        document = document.WithItems([box with { X = 400, Y = 30 }]);

        DocumentState next = ItemEdits.CopyToEveryPage(document, TestData.Set(box.Id), SizeOf, s_measurer, out _);

        PageRect copy = ItemGeometry.Bounds(Assert.Single(next.ItemsOn(tiny)), s_measurer);
        Assert.True(copy.X >= 0 && copy.Right <= 100.001 && copy.Y >= 0 && copy.Bottom <= 100.001, $"{copy}");
    }

    [Fact]
    public void Nothing_selected_changes_nothing()
    {
        DocumentState document = TestData.FourPagesWithItems();
        DocumentState next = ItemEdits.CopyToEveryPage(document, TestData.Set<ItemId>(), _ => TestData.A4, s_measurer, out ImmutableArray<ItemId> added);
        Assert.Same(document, next);
        Assert.Empty(added);
    }
}
