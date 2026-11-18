using Fillsign.Core.Model;

namespace Fillsign.Core.Geometry;

/// <summary>
/// Converts between display space (what the editor shows: PDF points from the top-left of the page after its own
/// /Rotate and the user's extra rotation, y down) and PDF user space (y up, unrotated, crop box anywhere).
/// This is the one place those three things meet (FR-8.8), so screen and export always agree.
/// </summary>
public sealed class PageTransform
{
    private readonly PdfBox _crop;

    public PageTransform(SourcePage source, Rotation extra)
    {
        ArgumentNullException.ThrowIfNull(source);
        _crop = source.CropBox;
        TotalRotation = source.Rotate + extra;
        DisplaySize = new PageSize(_crop.Width, _crop.Height).Turned(TotalRotation);
        DisplayToPdf = BuildDisplayToPdf();
        PdfToDisplay = DisplayToPdf.Invert();
    }

    /// <summary>The page's /Rotate plus the extra rotation: what a viewer shows.</summary>
    public Rotation TotalRotation { get; }

    /// <summary>The page's size as displayed.</summary>
    public PageSize DisplaySize { get; }

    public Affine DisplayToPdf { get; }

    public Affine PdfToDisplay { get; }

    public (double X, double Y) ToPdf(PagePoint display) => DisplayToPdf.Apply(display.X, display.Y);

    public PagePoint ToDisplay(double pdfX, double pdfY)
    {
        (double x, double y) = PdfToDisplay.Apply(pdfX, pdfY);
        return new PagePoint(x, y);
    }

    /// <summary>
    /// Maps an item's local coordinates (y down, origin at the item's top-left) to PDF user space. Exported content
    /// is drawn in item-local space with this matrix, so it lands where the editor showed it.
    /// </summary>
    public Affine ItemToPdf(PagePoint itemOrigin) => Affine.Compose(Affine.Translate(itemOrigin.X, itemOrigin.Y), DisplayToPdf);

    private Affine BuildDisplayToPdf()
    {
        double w = _crop.Width;
        double h = _crop.Height;

        // First undo the rotation: display (x, y) → (u, v) on the unrotated page, still y down from its top-left.
        // A clockwise quarter turn moves the unrotated top-left corner to the display's top-right.
        Affine displayToUnrotated = TotalRotation.QuarterTurns switch
        {
            0 => Affine.Identity,
            1 => new Affine(0, -1, 1, 0, 0, h),  // u = y, v = h − x
            2 => new Affine(-1, 0, 0, -1, w, h), // u = w − x, v = h − y
            _ => new Affine(0, 1, -1, 0, w, 0),  // u = w − y, v = x
        };

        // Then flip y and move to the crop box: pdfX = left + u, pdfY = top − v.
        var unrotatedToPdf = new Affine(1, 0, 0, -1, _crop.Left, _crop.Top);
        return Affine.Compose(displayToUnrotated, unrotatedToPdf);
    }
}
