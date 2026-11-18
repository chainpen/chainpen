using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace Fillsign.Pdf.Fonts;

/// <summary>
/// HarfBuzz font data in native memory that Fillsign owns. HarfBuzz reads a font's tables straight from this memory
/// for as long as the face lives; handing it managed memory risks the garbage collector moving or releasing it under
/// HarfBuzz, which crashed text shaping (SIGBUS in hb_shape_full, see KANBAN FS-026).
/// </summary>
internal static class NativeFontBlob
{
    /// <summary>A read-only blob over a native copy of <paramref name="fontFile"/>, freed when HarfBuzz is done with it.</summary>
    public static unsafe Blob Create(ReadOnlySpan<byte> fontFile)
    {
        if (fontFile.IsEmpty)
        {
            throw new ArgumentException("The font file is empty.", nameof(fontFile));
        }

        void* data = NativeMemory.Alloc((nuint)fontFile.Length);
        fontFile.CopyTo(new Span<byte>(data, fontFile.Length));
        nint address = (nint)data;
        return new Blob(address, fontFile.Length, MemoryMode.ReadOnly, () => NativeMemory.Free((void*)address));
    }
}
