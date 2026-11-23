namespace Chainpen.Core.Geometry;

/// <summary>
/// A 2D affine transform in PDF's matrix convention: x' = A·x + C·y + E, y' = B·x + D·y + F.
/// The six values can be written straight into a PDF <c>cm</c> operator or a PDFium matrix.
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>Applies <paramref name="first"/>, then <paramref name="then"/>.</summary>
    public static Affine Compose(Affine first, Affine then) => new(
        (first.A * then.A) + (first.B * then.C),
        (first.A * then.B) + (first.B * then.D),
        (first.C * then.A) + (first.D * then.C),
        (first.C * then.B) + (first.D * then.D),
        (first.E * then.A) + (first.F * then.C) + then.E,
        (first.E * then.B) + (first.F * then.D) + then.F);

    public Affine Invert()
    {
        double det = (A * D) - (B * C);
        if (det == 0)
        {
            throw new InvalidOperationException("The transform can't be inverted.");
        }

        return new Affine(
            D / det,
            -B / det,
            -C / det,
            A / det,
            ((C * F) - (D * E)) / det,
            ((B * E) - (A * F)) / det);
    }
}
