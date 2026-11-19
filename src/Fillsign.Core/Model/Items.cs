using System.Collections.Immutable;

namespace Fillsign.Core.Model;

public enum MarkKind
{
    Tick,
    Cross,
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,

    /// <summary>See-through yellow, drawn with Multiply so text underneath stays readable.</summary>
    Highlight,

    /// <summary>Opaque white box that covers marks on a scan.</summary>
    Whiteout,
}

public enum SignatureKind
{
    Signature,
    Initials,
}

/// <summary>
/// Something the user added to a page. Positions and sizes are in PDF points measured from the top-left of the page
/// as displayed (after all rotation), y down. They're converted to PDF user space only on export, by
/// <see cref="Geometry.PageTransform"/>.
/// </summary>
/// <remarks>Closed, so a <c>switch</c> over item types that misses a case doesn't compile.</remarks>
public closed record Item(ItemId Id, PageId Page, double X, double Y)
{
    public PagePoint Position => new(X, Y);
}

/// <summary>How the lines of a text item line up with each other (FS-952).</summary>
public enum TextAlign
{
    Left,
    Centre,
    Right,
}

/// <summary>
/// Typed text. Lines are separated by '\n'. Bold text is the same font drawn with an outline, so it measures the
/// same as regular text (FS-952); alignment lines up each line inside the width of the longest one.
/// </summary>
public sealed record TextItem(
    ItemId Id,
    PageId Page,
    double X,
    double Y,
    string Text,
    double FontSize,
    InkColor Color,
    bool IsBold = false,
    TextAlign Align = TextAlign.Left)
    : Item(Id, Page, X, Y)
{
    public const double DefaultFontSize = 12;

    /// <summary>
    /// The longest text one item holds. The working copy's reader refuses longer text, so the editor stops at the same
    /// length: otherwise a save would succeed and reopening it would lose every edit.
    /// </summary>
    public const int MaxLength = 10_000;

    public const double MinFontSize = 6;
    public const double MaxFontSize = 72;

    /// <summary>Line height as a multiple of the font size; screen and export share it.</summary>
    public const double LineHeight = 1.2;

    /// <summary>The width of bold's outline as a share of the font size, on screen and in the final copy.</summary>
    public const double BoldStroke = 0.05;

    public string[] Lines => Text.Split('\n');
}

/// <summary>A tick or cross, drawn in a square of <see cref="Size"/> points.</summary>
public sealed record MarkItem(ItemId Id, PageId Page, double X, double Y, MarkKind Kind, double Size, InkColor Color)
    : Item(Id, Page, X, Y)
{
    /// <summary>Suits a typical form checkbox.</summary>
    public const double DefaultSize = 14;
    public const double MinSize = 6;
    public const double MaxSize = 72;
}

/// <summary>A signature or initials image: PNG with a transparent background, cropped to the ink (FR-4.4).</summary>
public sealed record SignatureItem(ItemId Id, PageId Page, double X, double Y, SignatureKind Kind, SignatureImage Image, double Width, double Height)
    : Item(Id, Page, X, Y);

/// <summary>A picture pasted from another app (FR-7.14, FS-715): PNG, kept with its proportions.</summary>
public sealed record PictureItem(ItemId Id, PageId Page, double X, double Y, SignatureImage Image, double Width, double Height)
    : Item(Id, Page, X, Y)
{
    /// <summary>Pasted pictures start no wider than this, so a screenshot doesn't cover the whole page.</summary>
    public const double MaxStartWidth = 300;
}

/// <summary>
/// The image behind a signature or a pasted picture. Shared by every placed copy, so equality is by <see cref="Id"/>, not by bytes.
/// </summary>
public sealed record SignatureImage(Guid Id, ImmutableArray<byte> Png, int PixelWidth, int PixelHeight)
{
    public bool Equals(SignatureImage? other) => other is not null && other.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// A rectangle, ellipse, line, arrow, highlight or whiteout in a box. Lines and arrows run from one corner of the box
/// to the opposite one; <see cref="FlipX"/> and <see cref="FlipY"/> say which.
/// </summary>
public sealed record ShapeItem(
    ItemId Id,
    PageId Page,
    double X,
    double Y,
    ShapeKind Kind,
    double Width,
    double Height,
    InkColor Color,
    double StrokeWidth,
    bool FlipX,
    bool FlipY) : Item(Id, Page, X, Y)
{
    public bool IsFilled => Kind is ShapeKind.Highlight or ShapeKind.Whiteout;

    public bool IsLine => Kind is ShapeKind.Line or ShapeKind.Arrow;
}

/// <summary>
/// Freehand pen strokes. Points are fractions (0–1) of <see cref="Width"/> and <see cref="Height"/>, so the drawing
/// scales with its box.
/// </summary>
public sealed record InkItem(
    ItemId Id,
    PageId Page,
    double X,
    double Y,
    double Width,
    double Height,
    ImmutableArray<ImmutableArray<PagePoint>> Strokes,
    InkColor Color,
    double StrokeWidth) : Item(Id, Page, X, Y)
{
    public const double DefaultStrokeWidth = 2;
    public const double MinStrokeWidth = 0.5;
    public const double MaxStrokeWidth = 20;

    // ImmutableArray compares by reference; compare the points so undo snapshots and tests behave.
    public bool Equals(InkItem? other) =>
        other is not null
        && base.Equals(other)
        && Width == other.Width
        && Height == other.Height
        && Color == other.Color
        && StrokeWidth == other.StrokeWidth
        && Strokes.Length == other.Strokes.Length
        && Strokes.Zip(other.Strokes).All(pair => pair.First.SequenceEqual(pair.Second));

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Width, Height, Strokes.Length);
}
