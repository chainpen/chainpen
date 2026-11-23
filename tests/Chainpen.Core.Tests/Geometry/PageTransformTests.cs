using Chainpen.Core.Geometry;
using Chainpen.Core.Model;

namespace Chainpen.Core.Tests.Geometry;

/// <summary>FS-102: every combination of /Rotate, extra rotation and an offset crop box.</summary>
public sealed class PageTransformTests
{
    // An offset crop box (as on many scans and cropped PDFs): 400 wide, 300 tall, starting at (50, 70).
    private static readonly PdfBox s_crop = new(50, 70, 450, 370);

    public static TheoryData<int, int> AllRotations()
    {
        var data = new TheoryData<int, int>();
        foreach (int rotate in (int[])[0, 90, 180, 270])
        {
            foreach (int extra in (int[])[0, 90, 180, 270])
            {
                data.Add(rotate, extra);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllRotations))]
    public void Display_corners_map_to_the_rotated_crop_box_corners(int rotate, int extra)
    {
        var transform = new PageTransform(new SourcePage(s_crop, Rotation.FromDegrees(rotate)), Rotation.FromDegrees(extra));
        int total = (rotate + extra) % 360;

        // Where the display's top-left corner lands in PDF space for each total rotation (y up).
        (double X, double Y) expectedTopLeft = total switch
        {
            0 => (s_crop.Left, s_crop.Top),
            90 => (s_crop.Left, s_crop.Bottom),
            180 => (s_crop.Right, s_crop.Bottom),
            _ => (s_crop.Right, s_crop.Top),
        };

        AssertClose(expectedTopLeft, transform.ToPdf(new PagePoint(0, 0)));
        PageSize size = transform.DisplaySize;
        Assert.Equal(total % 180 == 0 ? new PageSize(400, 300) : new PageSize(300, 400), size);

        // The display's opposite corner is the opposite PDF corner.
        (double X, double Y) opposite = (s_crop.Left + s_crop.Right - expectedTopLeft.X, s_crop.Bottom + s_crop.Top - expectedTopLeft.Y);
        AssertClose(opposite, transform.ToPdf(new PagePoint(size.Width, size.Height)));
    }

    [Theory]
    [MemberData(nameof(AllRotations))]
    public void Points_round_trip(int rotate, int extra)
    {
        var transform = new PageTransform(new SourcePage(s_crop, Rotation.FromDegrees(rotate)), Rotation.FromDegrees(extra));
        var display = new PagePoint(37.5, 112.25);
        (double x, double y) = transform.ToPdf(display);
        PagePoint back = transform.ToDisplay(x, y);
        Assert.Equal(display.X, back.X, 9);
        Assert.Equal(display.Y, back.Y, 9);
    }

    [Theory]
    [MemberData(nameof(AllRotations))]
    public void Moving_right_on_screen_moves_along_the_turned_axis(int rotate, int extra)
    {
        var transform = new PageTransform(new SourcePage(s_crop, Rotation.FromDegrees(rotate)), Rotation.FromDegrees(extra));
        (double x0, double y0) = transform.ToPdf(new PagePoint(10, 10));
        (double x1, double y1) = transform.ToPdf(new PagePoint(11, 10));

        // Display "right" is PDF +x at 0°; at 90° (turned clockwise) it runs up the unrotated left edge, so +y.
        (double dx, double dy) expected = ((rotate + extra) % 360) switch
        {
            0 => (1, 0),
            90 => (0, 1),
            180 => (-1, 0),
            _ => (0, -1),
        };
        Assert.Equal(expected.dx, x1 - x0, 9);
        Assert.Equal(expected.dy, y1 - y0, 9);
    }

    [Fact]
    public void Item_matrix_places_item_origin_and_keeps_y_down()
    {
        var transform = new PageTransform(new SourcePage(new PdfBox(0, 0, 595, 842), Rotation.None), Rotation.None);
        Affine matrix = transform.ItemToPdf(new PagePoint(100, 200));
        AssertClose((100, 642), matrix.Apply(0, 0));
        AssertClose((110, 632), matrix.Apply(10, 10));
    }

    [Fact]
    public void Affine_inverse_and_compose_agree()
    {
        var m = new Affine(0, -1, 1, 0, 30, 400);
        Affine identity = Affine.Compose(m, m.Invert());
        AssertClose((5, 7), identity.Apply(5, 7));
        Assert.Throws<InvalidOperationException>(() => new Affine(0, 0, 0, 0, 1, 1).Invert());
    }

    private static void AssertClose((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
    }
}
