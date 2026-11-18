using System.Collections.Immutable;
using Fillsign.Core.Model;

namespace Fillsign.Core.Tests;

internal static class TestData
{
    public static readonly PageSize A4 = new(595, 842);

    public static TextItem Text(PageId page, double x = 100, double y = 100, string text = "Jane Citizen") =>
        new(ItemId.New(), page, x, y, text, 12, InkColor.Blue);

    public static MarkItem Tick(PageId page, double x = 50, double y = 50) =>
        new(ItemId.New(), page, x, y, MarkKind.Tick, MarkItem.DefaultSize, InkColor.Blue);

    public static ShapeItem Rectangle(PageId page, double x = 10, double y = 20, double w = 80, double h = 40) =>
        new(ItemId.New(), page, x, y, ShapeKind.Rectangle, w, h, InkColor.Red, 2, false, false);

    public static SignatureImage Png() =>
        new(Guid.NewGuid(), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3], 48, 16);

    public static SignatureItem Signature(PageId page) =>
        new(ItemId.New(), page, 200, 300, SignatureKind.Signature, Png(), 120, 40);

    public static InkItem Ink(PageId page) =>
        new(ItemId.New(), page, 30, 40, 100, 50, [[new(0, 0), new(0.5, 1), new(1, 0.25)], [new(0.2, 0.2)]], InkColor.Black, 2);

    /// <summary>Four pages (A–D) with one item of every kind spread over them.</summary>
    public static DocumentState FourPagesWithItems()
    {
        var document = DocumentState.ForSourcePages(4);
        PageId a = document.Pages[0].Id, b = document.Pages[1].Id, c = document.Pages[2].Id, d = document.Pages[3].Id;
        return document.WithItems([Tick(a), Text(a), Signature(b), Rectangle(c), Ink(d)]);
    }

    public static ImmutableHashSet<T> Set<T>(params T[] values) => [.. values];

    /// <summary>A minimal, valid one-page PDF with a classic cross-reference table.</summary>
    public static byte[] MinimalPdf()
    {
        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]>>",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = text.Length;
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<</Size {objects.Length + 1}/Root 1 0 R/ID[<0123456789abcdef0123456789abcdef><0123456789abcdef0123456789abcdef>]>>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }
}
