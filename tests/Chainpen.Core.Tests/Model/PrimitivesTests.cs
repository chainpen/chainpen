using System.Collections.Immutable;
using Chainpen.Core.Model;

namespace Chainpen.Core.Tests.Model;

public sealed class PrimitivesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 1)]
    [InlineData(-90, 3)]
    [InlineData(450, 1)]
    [InlineData(270, 3)]
    public void Rotation_normalises_degrees(int degrees, int quarterTurns) =>
        Assert.Equal(quarterTurns, Rotation.FromDegrees(degrees).QuarterTurns);

    [Fact]
    public void Rotation_rejects_non_right_angles() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Rotation.FromDegrees(45));

    [Fact]
    public void Rotation_turns_both_ways()
    {
        Assert.Equal(Rotation.FromDegrees(270), Rotation.None.CounterClockwise());
        Assert.Equal(Rotation.None, Rotation.FromDegrees(270).Clockwise());
        Assert.Equal(Rotation.FromDegrees(90), Rotation.FromDegrees(180) + Rotation.FromDegrees(270));
    }

    [Fact]
    public void Colour_round_trips_through_hex()
    {
        Assert.Equal("#1d3f9e", InkColor.Blue.ToHex());
        Assert.True(InkColor.TryParseHex("#B42318", out InkColor red));
        Assert.Equal(InkColor.Red, red);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1a3fa8")]
    [InlineData("#1a3fa")]
    [InlineData("#1a3fa8ff")]
    [InlineData("#zzzzzz")]
    [InlineData("#-12345")]
    public void Colour_rejects_bad_hex(string? hex) => Assert.False(InkColor.TryParseHex(hex, out _));

    [Fact]
    public void Ink_items_compare_by_points()
    {
        var page = PageId.New();
        InkItem a = TestData.Ink(page);
        InkItem b = a with { Strokes = [.. a.Strokes.Select(s => s.ToArray().ToImmutableArray())] };
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Strokes = [[new PagePoint(0, 0)]] });
    }

    [Fact]
    public void Document_states_compare_by_contents()
    {
        DocumentState a = TestData.FourPagesWithItems();
        DocumentState b = new([.. a.Pages], [.. a.Items]);
        Assert.Equal(a, b);
        Assert.True(a.ContainsSignature);
        Assert.True(a.HasItems(a.Pages[0].Id));
    }
}
