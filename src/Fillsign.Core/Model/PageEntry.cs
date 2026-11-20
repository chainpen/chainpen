namespace Fillsign.Core.Model;

/// <summary>
/// A page in the edited document: which page of the source PDF it shows, plus the extra rotation the user added on
/// top of the page's own /Rotate.
/// </summary>
/// <param name="Id">Items attach to this, not to the source index, so duplicates have their own items.</param>
/// <param name="SourceIndex">0-based page index in the source PDF.</param>
/// <param name="Rotation">Extra clockwise rotation added in Fillsign.</param>
public sealed record PageEntry(PageId Id, int SourceIndex, Rotation Rotation)
{
    public static PageEntry ForSource(int sourceIndex) => new(PageId.New(), sourceIndex, Rotation.None);
}

/// <summary>What Fillsign needs to know about a source page: its crop box and /Rotate.</summary>
/// <param name="CropBox">The visible area in PDF user space (y up): left, bottom, right, top.</param>
/// <param name="Rotate">The page's own /Rotate.</param>
public sealed record SourcePage(PdfBox CropBox, Rotation Rotate)
{
    /// <summary>Size as displayed with the page's own /Rotate, before any extra rotation.</summary>
    public PageSize DisplaySize => new PageSize(CropBox.Width, CropBox.Height).Turned(Rotate);
}

/// <summary>A rectangle in PDF user space (y up).</summary>
public readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}
