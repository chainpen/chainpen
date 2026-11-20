using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fillsign.Pdf.Export;

/// <summary>
/// Cuts a TrueType font down to the characters a document uses, so the PDFium writer embeds a few KB instead of
/// the whole font. Uses hb-subset from the native HarfBuzz library that ships with Avalonia (HarfBuzzSharp
/// exposes no managed wrapper for it). The result keeps a cmap for the retained characters, so PDFium can
/// map text to glyphs and generate /ToUnicode itself.
/// </summary>
public static partial class FontSubsetter
{
    private const string Lib = "libHarfBuzzSharp";

    // hb_subset_flags_t
    private const uint NoHinting = 0x1;

    /// <param name="faceIndex">Which font in a collection (.ttc); 0 for a single font file.</param>
    public static byte[] Subset(ReadOnlySpan<byte> font, IEnumerable<int> codepoints, int faceIndex = 0)
    {
        unsafe
        {
            fixed (byte* data = font)
            {
                // HB_MEMORY_MODE_DUPLICATE (0): HarfBuzz copies the bytes, so the pin can end with this scope.
                nint blob = hb_blob_create(data, (uint)font.Length, 0, 0, 0);
                nint face = hb_face_create(blob, (uint)faceIndex);
                nint input = hb_subset_input_create_or_fail();
                nint subset = 0, subsetBlob = 0;
                try
                {
                    if (input == 0)
                    {
                        throw new InvalidOperationException("HarfBuzz could not create a subset input.");
                    }

                    nint unicodes = hb_subset_input_unicode_set(input);
                    foreach (int codepoint in codepoints)
                    {
                        hb_set_add(unicodes, (uint)codepoint);
                    }

                    // Hinting is ignored by PDF viewers' rasterisers at typical sizes and roughly doubles the size.
                    hb_subset_input_set_flags(input, NoHinting);

                    subset = hb_subset_or_fail(face, input);
                    if (subset == 0)
                    {
                        throw new InvalidOperationException("HarfBuzz could not subset the font.");
                    }

                    subsetBlob = hb_face_reference_blob(subset);
                    byte* bytes = hb_blob_get_data(subsetBlob, out uint length);
                    return new ReadOnlySpan<byte>(bytes, (int)length).ToArray();
                }
                finally
                {
                    if (subsetBlob != 0)
                    {
                        hb_blob_destroy(subsetBlob);
                    }

                    if (subset != 0)
                    {
                        hb_face_destroy(subset);
                    }

                    if (input != 0)
                    {
                        hb_subset_input_destroy(input);
                    }

                    hb_face_destroy(face);
                    hb_blob_destroy(blob);
                }
            }
        }
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial nint hb_blob_create(byte* data, uint length, int mode, nint userData, nint destroy);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial byte* hb_blob_get_data(nint blob, out uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void hb_blob_destroy(nint blob);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint hb_face_create(nint blob, uint index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint hb_face_reference_blob(nint face);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void hb_face_destroy(nint face);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint hb_subset_input_create_or_fail();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint hb_subset_input_unicode_set(nint input);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void hb_subset_input_set_flags(nint input, uint flags);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void hb_subset_input_destroy(nint input);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void hb_set_add(nint set, uint codepoint);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint hb_subset_or_fail(nint source, nint input);
}
