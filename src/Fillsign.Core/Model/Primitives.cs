using System.Globalization;

namespace Fillsign.Core.Model;

/// <summary>Identifies an item for its whole life, through moves, undo and copy/paste.</summary>
public readonly record struct ItemId(Guid Value)
{
    public static ItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// Identifies a page in the edited document. Items belong to a page by this id, so they follow it through
/// reordering, rotation, duplication and merging (FR-6.10). Two duplicates of one source page have different ids.
/// </summary>
public readonly record struct PageId(Guid Value)
{
    public static PageId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>A point in page space: PDF points from the top-left of the page as displayed, y down.</summary>
public readonly record struct PagePoint(double X, double Y)
{
    public static PagePoint operator +(PagePoint a, PagePoint b) => new(a.X + b.X, a.Y + b.Y);

    public static PagePoint operator -(PagePoint a, PagePoint b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>A size in PDF points.</summary>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>The size after a quarter turn swaps width and height.</summary>
    public PageSize Turned(Rotation rotation) => rotation.IsQuarterTurn ? new PageSize(Height, Width) : this;
}

/// <summary>An axis-aligned rectangle in page space.</summary>
public readonly record struct PageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public PagePoint Centre => new(X + (Width / 2), Y + (Height / 2));

    public bool Contains(PagePoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool Contains(PageRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool Intersects(PageRect other) => other.X <= Right && other.Right >= X && other.Y <= Bottom && other.Bottom >= Y;

    /// <summary>The rectangle spanned by two corners in any order (a drag).</summary>
    public static PageRect FromCorners(PagePoint a, PagePoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
}

/// <summary>Clockwise rotation in quarter turns. Always 0, 90, 180 or 270 degrees.</summary>
public readonly record struct Rotation
{
    public static readonly Rotation None = new(0);

    private Rotation(int quarterTurns) => QuarterTurns = quarterTurns;

    /// <summary>0 to 3.</summary>
    public int QuarterTurns { get; }

    public int Degrees => QuarterTurns * 90;

    public bool IsQuarterTurn => QuarterTurns % 2 == 1;

    public static Rotation FromQuarterTurns(int quarterTurns) => new(((quarterTurns % 4) + 4) % 4);

    /// <summary>Accepts any multiple of 90 (PDF /Rotate allows negative values and values over 360).</summary>
    public static Rotation FromDegrees(int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Rotation must be a multiple of 90 degrees.");
        }

        return FromQuarterTurns(degrees / 90);
    }

    public Rotation Clockwise() => FromQuarterTurns(QuarterTurns + 1);

    public Rotation CounterClockwise() => FromQuarterTurns(QuarterTurns - 1);

    public static Rotation operator +(Rotation a, Rotation b) => FromQuarterTurns(a.QuarterTurns + b.QuarterTurns);

    public override string ToString() => $"{Degrees}°";
}

/// <summary>An opaque ink colour. The palette is fixed (FR-3.9); highlight yellow is drawn separately.</summary>
public readonly record struct InkColor(byte R, byte G, byte B)
{
    // Design system tokens ink-black, ink-blue and ink-red; the same in light and dark (the page is never themed).
    public static readonly InkColor Black = new(0x11, 0x13, 0x18);
    public static readonly InkColor Blue = new(0x1d, 0x3f, 0x9e);
    public static readonly InkColor Red = new(0xb4, 0x23, 0x18);

    // FS-952: two more inks, dark enough to read on white and to tell apart from the others in print.
    public static readonly InkColor Green = new(0x1a, 0x6b, 0x3a);
    public static readonly InkColor Purple = new(0x5b, 0x2a, 0x86);
    public static readonly InkColor White = new(0xff, 0xff, 0xff);

    /// <summary>
    /// Black until the person picks another colour; the app then remembers their last choice (INTERACTION-PATTERNS §3).
    /// </summary>
    public static InkColor Default => Black;

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    public static bool TryParseHex(string? hex, out InkColor color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return false;
        }

        if (!int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        color = new InkColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() => ToHex();
}
