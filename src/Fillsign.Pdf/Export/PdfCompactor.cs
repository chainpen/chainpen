using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Export;

/// <summary>
/// Makes a PDF that PDFium wrote smaller without changing what's in it (FS-967). PDFium writes every object as plain
/// text with a classic cross-reference table, so a form that came packed in compressed object streams (PDF 1.5) can
/// come out several times larger: the IRS W-4 went from 209 KB to 560 KB untouched. This packs the plain objects back
/// into compressed object streams, indexes them with a compressed cross-reference stream, and compresses the content
/// streams PDFium left uncompressed (the form fields' appearances).
/// </summary>
/// <remarks>
/// Anything it doesn't expect (an encrypted file, more than one cross-reference section, a stream whose length doesn't
/// add up) leaves the file as it was. The result is reopened with PDFium and must have the same pages; if not, the
/// original is returned. A smaller file is only ever a nicety, never worth a broken one.
/// </remarks>
public static class PdfCompactor
{
    // Objects per object stream: big enough to compress well, small enough that a reader unpacks little to reach one.
    private const int ObjectsPerStream = 100;

    // Streams smaller than this aren't worth a filter.
    private const int MinStreamToCompress = 64;

    /// <summary>The same document, smaller; or <paramref name="pdf"/> itself if it can't be made smaller safely.</summary>
    public static byte[] Compact(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            if (Read(pdf) is not { } document)
            {
                return pdf;
            }

            byte[] compact = Write(document);
            return compact.Length < pdf.Length && HasSamePages(pdf, compact) ? compact : pdf;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            // Something this reader doesn't expect: keep the file as PDFium wrote it.
            System.Diagnostics.Trace.TraceWarning($"A saved PDF couldn't be made smaller: {ex.Message}");
            return pdf;
        }
    }

    /// <summary>
    /// Cuts the update section PDFium appended after the first <paramref name="baseLength"/> bytes down to the objects
    /// that actually changed, packed as <see cref="Compact"/> packs them. PDFium's incremental save writes out every
    /// object it loaded, changed or not: filling one field on the IRS W-4 rewrote 165 unchanged objects, 108 KB, for 2
    /// that changed. The first <paramref name="baseLength"/> bytes are kept exactly; or <paramref name="pdf"/> itself
    /// is returned if the update can't be made smaller safely.
    /// </summary>
    /// <param name="pdf">A file PDFium saved incrementally.</param>
    /// <param name="baseLength">The length of the file it was opened from, a single section with no updates of its own.</param>
    public static byte[] CompactUpdate(byte[] pdf, int baseLength)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseLength, pdf.Length);
        try
        {
            if (ReadUpdate(pdf, baseLength) is not { } update)
            {
                return pdf;
            }

            using var output = new MemoryStream(pdf.Length);
            output.Write(pdf.AsSpan(0, baseLength));
            if (pdf[baseLength - 1] is not ((byte)'\n' or (byte)'\r'))
            {
                Write(output, "\n");
            }

            WritePacked(output, pdf, update.Changed, update.Freed, update.NextNumber, update.Trailer, update.PreviousXref);
            byte[] compact = output.ToArray();
            return compact.Length < pdf.Length && HasSamePages(pdf, compact) ? compact : pdf;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            System.Diagnostics.Trace.TraceWarning($"A saved PDF's update couldn't be made smaller: {ex.Message}");
            return pdf;
        }
    }

    private static bool IsUnreadable(Exception ex) => ex is FormatException or InvalidOperationException or IndexOutOfRangeException
        or ArgumentException or OverflowException or KeyNotFoundException or InvalidDataException;

    private static bool HasSamePages(byte[] original, byte[] compact)
    {
        try
        {
            using PdfiumDocument before = PdfiumDocument.Open(original);
            using PdfiumDocument after = PdfiumDocument.Open(compact);
            return before.PageCount == after.PageCount;
        }
        catch (Exception ex) when (ex is PdfOpenException or InvalidOperationException)
        {
            return false;
        }
    }

    // ----- Reading what PDFium wrote -----

    private sealed record PdfObject(int Number, int Generation, int ValueStart, int ValueEnd, Stream? Stream);

    /// <summary>A stream: its dictionary (inclusive span) and its data.</summary>
    private sealed record Stream(int DictionaryStart, int DictionaryEnd, int DataStart, int DataLength);

    private sealed record Document(byte[] Bytes, string Version, IReadOnlyList<PdfObject> Objects, Dictionary<string, (int Start, int End)> Trailer);

    private static Document? Read(byte[] pdf)
    {
        int xrefOffset = LastStartXref(pdf, pdf.Length);
        if (xrefOffset <= 0 || !StartsWith(pdf, xrefOffset, "xref"u8))
        {
            // A cross-reference stream already: written by something other than PDFium's plain writer.
            return null;
        }

        XrefSection section = ReadXref(pdf, xrefOffset);
        if (section.Trailer.ContainsKey("Encrypt") || section.Trailer.ContainsKey("Prev") || section.Trailer.ContainsKey("XRefStm"))
        {
            return null;
        }

        Dictionary<int, (int Offset, int Generation)> offsets = section.InFile();
        var objects = new List<PdfObject>(offsets.Count);
        foreach ((int number, (int offset, int generation)) in offsets.OrderBy(entry => entry.Key))
        {
            objects.Add(ReadObject(pdf, number, generation, offset, offsets));
        }

        return new Document(pdf, HeaderVersion(pdf), objects, section.Trailer);
    }

    /// <summary>Where the last "startxref" before <paramref name="end"/> points, or -1.</summary>
    private static int LastStartXref(byte[] pdf, int end)
    {
        int keyword = pdf.AsSpan(0, end).LastIndexOf("startxref"u8);
        if (keyword < 0)
        {
            return -1;
        }

        int position = SkipSpace(pdf, keyword + "startxref".Length);
        int offset = ReadInteger(pdf, ref position);
        return offset < end ? offset : -1;
    }

    /// <summary>One row of a cross-reference section: 0 free (and its generation), 1 at an offset, 2 in an object stream (and its index).</summary>
    private readonly record struct XrefEntry(int Kind, int Field2, int Field3);

    private sealed record XrefSection(SortedDictionary<int, XrefEntry> Entries, Dictionary<string, (int Start, int End)> Trailer)
    {
        /// <summary>The objects written out in the file itself, not in an object stream.</summary>
        public Dictionary<int, (int Offset, int Generation)> InFile() =>
            Entries.Where(entry => entry.Value.Kind == 1).ToDictionary(entry => entry.Key, entry => (entry.Value.Field2, entry.Value.Field3));
    }

    /// <summary>The cross-reference section at <paramref name="offset"/>: a classic table and trailer, or a cross-reference stream.</summary>
    private static XrefSection ReadXref(byte[] pdf, int offset)
    {
        var entries = new SortedDictionary<int, XrefEntry>();
        if (!StartsWith(pdf, offset, "xref"u8))
        {
            return ReadXrefStream(pdf, offset);
        }

        // The classic table: subsections of "first count", then 20-byte entries.
        int position = SkipSpace(pdf, offset + 4);
        while (!StartsWith(pdf, position, "trailer"u8))
        {
            int first = ReadInteger(pdf, ref position);
            position = SkipSpace(pdf, position);
            int count = ReadInteger(pdf, ref position);
            position = SkipSpace(pdf, position);
            for (int i = 0; i < count; i++)
            {
                int field2 = ReadInteger(pdf, ref position);
                position = SkipSpace(pdf, position);
                int generation = ReadInteger(pdf, ref position);
                position = SkipSpace(pdf, position);
                byte kind = pdf[position++];
                position = SkipSpace(pdf, position);
                entries[first + i] = new XrefEntry(kind == (byte)'n' ? 1 : 0, field2, generation);
            }
        }

        position = SkipSpace(pdf, position + "trailer".Length);
        return new XrefSection(entries, DictionaryEntries(pdf, position, SkipValue(pdf, position)));
    }

    private static XrefSection ReadXrefStream(byte[] pdf, int offset)
    {
        int position = SkipSpace(pdf, offset);
        int number = ReadInteger(pdf, ref position);
        position = SkipSpace(pdf, position);
        int generation = ReadInteger(pdf, ref position);
        PdfObject xref = ReadObject(pdf, number, generation, offset, []);
        Dictionary<string, (int Start, int End)> trailer = DictionaryEntries(pdf, xref.ValueStart, xref.ValueEnd);
        // PDFium leaves /Type /XRef off the cross-reference streams it writes for an update.
        bool isXref = Name(pdf, trailer, "Type") is "/XRef" or "" && trailer.ContainsKey("W");
        if (xref.Stream is not { } stream || !isXref || trailer.ContainsKey("DecodeParms"))
        {
            throw new FormatException("The cross-reference stream isn't one this reader handles.");
        }

        byte[] rows = StreamData(pdf, trailer, stream);
        int[] widths = Integers(pdf, trailer["W"]);
        int size = Integer(pdf, trailer["Size"]);
        int[] index = trailer.TryGetValue("Index", out (int Start, int End) indexValue) ? Integers(pdf, indexValue) : [0, size];
        if (widths.Length != 3 || index.Length % 2 != 0)
        {
            throw new FormatException("The cross-reference stream's /W or /Index is malformed.");
        }

        var entries = new SortedDictionary<int, XrefEntry>();
        int at = 0;
        for (int run = 0; run < index.Length; run += 2)
        {
            for (int i = 0; i < index[run + 1]; i++)
            {
                // A missing type field means type 1.
                int kind = widths[0] == 0 ? 1 : Field(rows, ref at, widths[0]);
                int field2 = Field(rows, ref at, widths[1]);
                int field3 = Field(rows, ref at, widths[2]);
                entries[index[run] + i] = new XrefEntry(kind, field2, field3);
            }
        }

        return new XrefSection(entries, trailer);
    }

    private static int Field(byte[] rows, ref int at, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | rows[at++];
        }

        return checked((int)value);
    }

    /// <summary>A stream's data, inflated if it's Flate-compressed; any other filter isn't read.</summary>
    private static byte[] StreamData(byte[] pdf, Dictionary<string, (int Start, int End)> entries, Stream stream)
    {
        ReadOnlySpan<byte> data = pdf.AsSpan(stream.DataStart, stream.DataLength);
        if (!entries.ContainsKey("Filter"))
        {
            return data.ToArray();
        }

        if (Name(pdf, entries, "Filter") != "/FlateDecode")
        {
            throw new FormatException("A stream this reader needs has a filter it doesn't read.");
        }

        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static PdfObject ReadObject(byte[] pdf, int number, int generation, int offset, Dictionary<int, (int Offset, int Generation)> offsets)
    {
        int position = SkipSpace(pdf, offset);
        if (ReadInteger(pdf, ref position) != number)
        {
            throw new FormatException($"Object {number} isn't where the table says.");
        }

        position = SkipSpace(pdf, position);
        ReadInteger(pdf, ref position);
        position = SkipSpace(pdf, position);
        if (!StartsWith(pdf, position, "obj"u8))
        {
            throw new FormatException($"Object {number} has no obj keyword.");
        }

        int valueStart = SkipSpace(pdf, position + 3);
        int valueEnd = SkipValue(pdf, valueStart);
        int after = SkipSpace(pdf, valueEnd);
        if (!StartsWith(pdf, after, "stream"u8))
        {
            return new PdfObject(number, generation, valueStart, valueEnd, null);
        }

        // The data starts after the stream keyword's end of line, and runs for /Length bytes.
        int dataStart = after + "stream".Length;
        if (pdf[dataStart] == '\r')
        {
            dataStart++;
        }

        if (pdf[dataStart] == '\n')
        {
            dataStart++;
        }

        Dictionary<string, (int Start, int End)> entries = DictionaryEntries(pdf, valueStart, valueEnd);
        if (!entries.TryGetValue("Length", out (int Start, int End) lengthValue))
        {
            throw new FormatException($"Stream {number} has no length.");
        }

        int length = ResolveInteger(pdf, lengthValue, offsets);
        int end = SkipSpace(pdf, dataStart + length);
        if (dataStart + length > pdf.Length || !StartsWith(pdf, end, "endstream"u8))
        {
            throw new FormatException($"Stream {number} doesn't end where its length says.");
        }

        return new PdfObject(number, generation, valueStart, valueEnd, new Stream(valueStart, valueEnd, dataStart, length));
    }

    private static int ResolveInteger(byte[] pdf, (int Start, int End) value, Dictionary<int, (int Offset, int Generation)> offsets)
    {
        int position = value.Start;
        int number = ReadInteger(pdf, ref position);
        if (SkipSpace(pdf, position) >= value.End)
        {
            return number;
        }

        // An indirect length: "12 0 R".
        if (!offsets.TryGetValue(number, out (int Offset, int Generation) target))
        {
            throw new FormatException("A stream's length refers to a missing object.");
        }

        int at = SkipSpace(pdf, target.Offset);
        ReadInteger(pdf, ref at);
        at = SkipSpace(pdf, at);
        ReadInteger(pdf, ref at);
        at = SkipSpace(pdf, SkipSpace(pdf, at) + 3);
        return ReadInteger(pdf, ref at);
    }

    // ----- Reading an update section -----

    private sealed record Update(IReadOnlyList<PdfObject> Changed, IReadOnlyDictionary<int, int> Freed, int NextNumber,
        Dictionary<string, (int Start, int End)> Trailer, int PreviousXref);

    private static Update? ReadUpdate(byte[] pdf, int baseLength)
    {
        int updateOffset = LastStartXref(pdf, pdf.Length);
        int baseOffset = LastStartXref(pdf, baseLength);
        if (updateOffset < baseLength || baseOffset <= 0)
        {
            return null;
        }

        XrefSection update = ReadXref(pdf, updateOffset);
        XrefSection original = ReadXref(pdf, baseOffset);
        bool isOneUpdateOnOneSection = update.Trailer.TryGetValue("Prev", out (int Start, int End) prev) && Integer(pdf, prev) == baseOffset
            && !(update.Trailer.ContainsKey("Encrypt") || update.Trailer.ContainsKey("XRefStm"))
            && !(original.Trailer.ContainsKey("Prev") || original.Trailer.ContainsKey("Encrypt") || original.Trailer.ContainsKey("XRefStm"));
        if (!isOneUpdateOnOneSection)
        {
            return null;
        }

        var originals = new OriginalObjects(pdf, original);
        Dictionary<int, (int Offset, int Generation)> offsets = update.InFile();
        var changed = new List<PdfObject>();
        var freed = new Dictionary<int, int>();
        foreach ((int number, XrefEntry entry) in update.Entries)
        {
            if (entry.Kind == 0)
            {
                freed[number] = entry.Field3;
                continue;
            }

            // PDFium writes an update's objects out plainly, after the original.
            if (entry.Kind != 1 || entry.Field2 < baseLength)
            {
                return null;
            }

            PdfObject item = ReadObject(pdf, number, entry.Field3, entry.Field2, offsets);
            if (entry.Field2 == updateOffset)
            {
                continue; // The update's own cross-reference stream: replaced by a new one.
            }

            if (!originals.IsUnchanged(item))
            {
                changed.Add(item);
            }
        }

        return new Update(changed, freed, Integer(pdf, update.Trailer["Size"]), update.Trailer, baseOffset);
    }

    /// <summary>The objects of a file's original section, for comparing an update's objects with; object streams are unpacked as needed.</summary>
    private sealed class OriginalObjects(byte[] pdf, XrefSection section)
    {
        private readonly Dictionary<int, (int Offset, int Generation)> _inFile = section.InFile();
        private readonly Dictionary<int, (byte[] Data, int First, int[] Header)> _unpacked = [];

        /// <summary>Whether <paramref name="item"/> is exactly what the original section already holds under its number.</summary>
        public bool IsUnchanged(PdfObject item)
        {
            if (!section.Entries.TryGetValue(item.Number, out XrefEntry entry))
            {
                return false;
            }

            if (entry.Kind == 1)
            {
                PdfObject before = ReadObject(pdf, item.Number, entry.Field3, entry.Field2, _inFile);
                return before.Generation == item.Generation && SameValue(pdf, before, item);
            }

            if (entry.Kind != 2 || item.Generation != 0 || item.Stream is not null)
            {
                return false;
            }

            (byte[] data, int first, int[] header) = Unpack(entry.Field2);
            int index = entry.Field3;
            if (2 * index + 1 >= header.Length || header[2 * index] != item.Number)
            {
                throw new FormatException($"Object {item.Number} isn't where the cross-reference stream says.");
            }

            int start = SkipSpace(data, first + header[2 * index + 1]);
            int end = SkipValue(data, start);
            return data.AsSpan(start, end - start).SequenceEqual(pdf.AsSpan(item.ValueStart, item.ValueEnd - item.ValueStart));
        }

        private static bool SameValue(byte[] pdf, PdfObject before, PdfObject after)
        {
            if (before.Stream is null || after.Stream is null)
            {
                return before.Stream is null && after.Stream is null && Span(pdf, before.ValueStart, before.ValueEnd).SequenceEqual(Span(pdf, after.ValueStart, after.ValueEnd));
            }

            // Streams compare by dictionary and data: PDFium frames the "stream" keyword differently in an update.
            return Span(pdf, before.Stream.DictionaryStart, before.Stream.DictionaryEnd).SequenceEqual(Span(pdf, after.Stream.DictionaryStart, after.Stream.DictionaryEnd))
                && pdf.AsSpan(before.Stream.DataStart, before.Stream.DataLength).SequenceEqual(pdf.AsSpan(after.Stream.DataStart, after.Stream.DataLength));
        }

        private static ReadOnlySpan<byte> Span(byte[] pdf, int start, int end) => pdf.AsSpan(start, end - start);

        private (byte[] Data, int First, int[] Header) Unpack(int streamNumber)
        {
            if (_unpacked.TryGetValue(streamNumber, out (byte[] Data, int First, int[] Header) unpacked))
            {
                return unpacked;
            }

            (int offset, int generation) = _inFile[streamNumber];
            PdfObject container = ReadObject(pdf, streamNumber, generation, offset, _inFile);
            Dictionary<string, (int Start, int End)> entries = DictionaryEntries(pdf, container.ValueStart, container.ValueEnd);
            if (container.Stream is not { } stream || Name(pdf, entries, "Type") != "/ObjStm")
            {
                throw new FormatException($"Object {streamNumber} isn't an object stream.");
            }

            byte[] data = StreamData(pdf, entries, stream);
            int first = Integer(pdf, entries["First"]);
            int count = Integer(pdf, entries["N"]);
            int[] header = new int[2 * count];
            int at = 0;
            for (int i = 0; i < header.Length; i++)
            {
                at = SkipSpace(data, at);
                header[i] = ReadInteger(data, ref at);
            }

            unpacked = (data, first, header);
            _unpacked[streamNumber] = unpacked;
            return unpacked;
        }
    }

    private static string Name(byte[] pdf, Dictionary<string, (int Start, int End)> entries, string key) =>
        entries.TryGetValue(key, out (int Start, int End) value) ? Encoding.Latin1.GetString(pdf, value.Start, value.End - value.Start) : "";

    private static int Integer(byte[] pdf, (int Start, int End) value)
    {
        int position = value.Start;
        int number = ReadInteger(pdf, ref position);
        return position == value.End ? number : throw new FormatException("A direct integer was expected.");
    }

    private static int[] Integers(byte[] pdf, (int Start, int End) array)
    {
        if (pdf[array.Start] != '[')
        {
            throw new FormatException("An array was expected.");
        }

        var values = new List<int>();
        int position = SkipSpace(pdf, array.Start + 1);
        while (pdf[position] != ']')
        {
            values.Add(ReadInteger(pdf, ref position));
            position = SkipSpace(pdf, position);
        }

        return [.. values];
    }

    // ----- Writing it back, packed -----

    private static byte[] Write(Document document)
    {
        byte[] pdf = document.Bytes;
        using var output = new MemoryStream(pdf.Length);
        string version = string.CompareOrdinal(document.Version, "1.5") < 0 ? "1.5" : document.Version;
        Write(output, $"%PDF-{version}\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        int maxNumber = document.Objects.Count == 0 ? 0 : document.Objects.Max(item => item.Number);
        WritePacked(output, pdf, document.Objects, new Dictionary<int, int>(), maxNumber + 1, document.Trailer, previousXref: null);
        return output.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="objects"/> (spans of <paramref name="pdf"/>) packed, then a cross-reference stream for them
    /// and the end of the file. New objects (the object streams, the cross-reference stream) are numbered from
    /// <paramref name="nextNumber"/>. With <paramref name="previousXref"/>, this is an update section: its
    /// cross-reference stream lists only these objects and the <paramref name="freed"/> ones, and points back to the
    /// previous section.
    /// </summary>
    private static void WritePacked(MemoryStream output, byte[] pdf, IReadOnlyList<PdfObject> objects, IReadOnlyDictionary<int, int> freed,
        int nextNumber, Dictionary<string, (int Start, int End)> trailerEntries, int? previousXref)
    {
        var entries = new Dictionary<int, (int Kind, long Field2, int Field3)>();
        foreach ((int number, int generation) in freed)
        {
            entries[number] = (0, 0, generation);
        }

        // Streams can't go in an object stream, nor can objects with a generation other than 0.
        foreach (PdfObject item in objects.Where(item => item.Stream is not null || item.Generation != 0))
        {
            entries[item.Number] = (1, output.Position, item.Generation);
            Write(output, $"{item.Number} {item.Generation} obj\n");
            if (item.Stream is { } stream)
            {
                WriteStream(output, pdf, stream);
            }
            else
            {
                output.Write(pdf.AsSpan(item.ValueStart, item.ValueEnd - item.ValueStart));
            }

            Write(output, "\nendobj\n");
        }

        // The rest go in compressed object streams, numbered after the document's own objects.
        PdfObject[] packable = [.. objects.Where(item => item.Stream is null && item.Generation == 0)];
        for (int start = 0; start < packable.Length; start += ObjectsPerStream)
        {
            PdfObject[] group = packable[start..Math.Min(packable.Length, start + ObjectsPerStream)];
            int streamNumber = nextNumber++;
            var header = new StringBuilder();
            using var bodies = new MemoryStream();
            for (int index = 0; index < group.Length; index++)
            {
                header.Append(CultureInfo.InvariantCulture, $"{group[index].Number} {bodies.Position} ");
                bodies.Write(pdf.AsSpan(group[index].ValueStart, group[index].ValueEnd - group[index].ValueStart));
                bodies.WriteByte((byte)'\n');
                entries[group[index].Number] = (2, streamNumber, index);
            }

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            byte[] packed = Deflate([.. headerBytes, .. bodies.ToArray()]);
            entries[streamNumber] = (1, output.Position, 0);
            Write(output, $"{streamNumber} 0 obj\n<< /Type /ObjStm /N {group.Length} /First {headerBytes.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
            output.Write(packed);
            Write(output, "\nendstream\nendobj\n");
        }

        // The cross-reference stream: one row per object number, type / offset or object stream / generation or index.
        int xrefNumber = nextNumber;
        long xrefOffset = output.Position;
        entries[xrefNumber] = (1, xrefOffset, 0);
        int size = xrefNumber + 1;
        int offsetBytes = BytesFor(Math.Max(xrefOffset, size));

        // A whole file lists every number, missing ones as free; an update lists only its own, in runs of /Index.
        int[] numbers = previousXref is null ? [.. Enumerable.Range(0, size)] : [.. entries.Keys.Order()];
        using var rows = new MemoryStream();
        foreach (int number in numbers)
        {
            (int kind, long field2, int field3) = entries.TryGetValue(number, out var entry) ? entry : (0, 0, number == 0 ? 65535 : 0);
            rows.WriteByte((byte)kind);
            for (int shift = (offsetBytes - 1) * 8; shift >= 0; shift -= 8)
            {
                rows.WriteByte((byte)(field2 >> shift));
            }

            rows.WriteByte((byte)(field3 >> 8));
            rows.WriteByte((byte)field3);
        }

        byte[] xref = Deflate(rows.ToArray());
        var trailer = new StringBuilder();
        foreach (string key in (string[])["Root", "Info", "ID"])
        {
            if (trailerEntries.TryGetValue(key, out (int Start, int End) value))
            {
                trailer.Append(CultureInfo.InvariantCulture, $" /{key} {Encoding.Latin1.GetString(pdf, value.Start, value.End - value.Start)}");
            }
        }

        if (previousXref is int previous)
        {
            trailer.Append(CultureInfo.InvariantCulture, $" /Index [{IndexRuns(numbers)}] /Prev {previous}");
        }

        Write(output, $"{xrefNumber} 0 obj\n<< /Type /XRef /Size {size} /W [1 {offsetBytes} 2]{trailer} /Filter /FlateDecode /Length {xref.Length} >>\nstream\n");
        output.Write(xref);
        Write(output, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
    }

    /// <summary>Sorted object numbers as /Index runs: "first count first count …".</summary>
    private static string IndexRuns(int[] numbers)
    {
        var runs = new StringBuilder();
        for (int start = 0; start < numbers.Length;)
        {
            int end = start + 1;
            while (end < numbers.Length && numbers[end] == numbers[end - 1] + 1)
            {
                end++;
            }

            runs.Append(CultureInfo.InvariantCulture, $"{(start == 0 ? "" : " ")}{numbers[start]} {end - start}");
            start = end;
        }

        return runs.ToString();
    }

    /// <summary>
    /// A stream as it was, or compressed if PDFium left it uncompressed: the form fields' appearances, mostly. XMP
    /// metadata stays readable, as the standard recommends.
    /// </summary>
    private static void WriteStream(MemoryStream output, byte[] pdf, Stream stream)
    {
        Dictionary<string, (int Start, int End)> entries = DictionaryEntries(pdf, stream.DictionaryStart, stream.DictionaryEnd);
        bool isMetadata = entries.TryGetValue("Type", out (int Start, int End) type) && Encoding.ASCII.GetString(pdf, type.Start, type.End - type.Start) == "/Metadata";
        ReadOnlySpan<byte> data = pdf.AsSpan(stream.DataStart, stream.DataLength);
        byte[]? compressed = !entries.ContainsKey("Filter") && !isMetadata && data.Length >= MinStreamToCompress ? Deflate(data) : null;
        if (compressed is null || compressed.Length >= data.Length)
        {
            output.Write(pdf.AsSpan(stream.DictionaryStart, stream.DictionaryEnd - stream.DictionaryStart));
            Write(output, "\nstream\n");
            output.Write(data);
            Write(output, "\nendstream");
            return;
        }

        // The same dictionary without its length, then the filter and the new length.
        Write(output, "<<");
        foreach ((string key, (int start, int end)) in entries.Where(entry => entry.Key != "Length").OrderBy(entry => entry.Value.Start))
        {
            Write(output, $" /{key} ");
            output.Write(pdf.AsSpan(start, end - start));
        }

        Write(output, $" /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        output.Write(compressed);
        Write(output, "\nendstream");
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
    }

    private static int BytesFor(long value)
    {
        int bytes = 1;
        while (value >= 1L << (8 * bytes))
        {
            bytes++;
        }

        return Math.Max(bytes, 2);
    }

    private static void Write(MemoryStream output, string text) => output.Write(Encoding.Latin1.GetBytes(text));

    // ----- A small PDF lexer: just enough to find where values end -----

    private static bool IsSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte value) => value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
        or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>Skips white space and comments.</summary>
    private static int SkipSpace(byte[] pdf, int position)
    {
        while (position < pdf.Length)
        {
            if (IsSpace(pdf[position]))
            {
                position++;
            }
            else if (pdf[position] == '%')
            {
                while (position < pdf.Length && pdf[position] is not ((byte)'\r' or (byte)'\n'))
                {
                    position++;
                }
            }
            else
            {
                break;
            }
        }

        return position;
    }

    /// <summary>The end of the value starting at <paramref name="position"/>, an indirect reference ("12 0 R") counting as one.</summary>
    private static int SkipValue(byte[] pdf, int position)
    {
        int end = SkipToken(pdf, position);
        if (IsInteger(pdf, position, end))
        {
            // "12 0 R" is one value.
            int second = SkipSpace(pdf, end);
            if (second >= pdf.Length || pdf[second] is < (byte)'0' or > (byte)'9')
            {
                return end;
            }

            int secondEnd = SkipToken(pdf, second);
            int third = SkipSpace(pdf, secondEnd);
            if (IsInteger(pdf, second, secondEnd) && third < pdf.Length && pdf[third] == 'R' && (third + 1 >= pdf.Length || IsSpace(pdf[third + 1]) || IsDelimiter(pdf[third + 1])))
            {
                return third + 1;
            }
        }

        return end;
    }

    private static int SkipToken(byte[] pdf, int position)
    {
        if (position >= pdf.Length)
        {
            throw new FormatException("The file ends in the middle of a value.");
        }

        switch (pdf[position])
        {
            case (byte)'<' when position + 1 < pdf.Length && pdf[position + 1] == '<':
                position = SkipSpace(pdf, position + 2);
                while (!(pdf[position] == '>' && pdf[position + 1] == '>'))
                {
                    position = SkipSpace(pdf, SkipValue(pdf, position));
                }

                return position + 2;
            case (byte)'<':
                return Array.IndexOf(pdf, (byte)'>', position) + 1;
            case (byte)'[':
                position = SkipSpace(pdf, position + 1);
                while (pdf[position] != ']')
                {
                    position = SkipSpace(pdf, SkipValue(pdf, position));
                }

                return position + 1;
            case (byte)'(':
                int depth = 0;
                for (; position < pdf.Length; position++)
                {
                    if (pdf[position] == '\\')
                    {
                        position++;
                    }
                    else if (pdf[position] == '(')
                    {
                        depth++;
                    }
                    else if (pdf[position] == ')' && --depth == 0)
                    {
                        return position + 1;
                    }
                }

                throw new FormatException("A string never ends.");
            case (byte)'/':
                position++;
                while (position < pdf.Length && !IsSpace(pdf[position]) && !IsDelimiter(pdf[position]))
                {
                    position++;
                }

                return position;
            default:
                int start = position;
                while (position < pdf.Length && !IsSpace(pdf[position]) && !IsDelimiter(pdf[position]))
                {
                    position++;
                }

                return position > start ? position : throw new FormatException($"Unexpected '{(char)pdf[start]}'.");
        }
    }

    /// <summary>A dictionary's entries: each key (without its slash) and the span of its value.</summary>
    private static Dictionary<string, (int Start, int End)> DictionaryEntries(byte[] pdf, int start, int end)
    {
        var entries = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        if (!StartsWith(pdf, start, "<<"u8))
        {
            return entries;
        }

        int position = SkipSpace(pdf, start + 2);
        while (position < end - 2)
        {
            int keyEnd = SkipToken(pdf, position);
            string key = Encoding.Latin1.GetString(pdf, position + 1, keyEnd - position - 1);
            int valueStart = SkipSpace(pdf, keyEnd);
            int valueEnd = SkipValue(pdf, valueStart);
            entries[key] = (valueStart, valueEnd);
            position = SkipSpace(pdf, valueEnd);
        }

        return entries;
    }

    private static bool IsInteger(byte[] pdf, int start, int end)
    {
        if (start >= end)
        {
            return false;
        }

        for (int i = start; i < end; i++)
        {
            if (pdf[i] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadInteger(byte[] pdf, ref int position)
    {
        int start = position;
        while (position < pdf.Length && pdf[position] is >= (byte)'0' and <= (byte)'9')
        {
            position++;
        }

        return position > start
            ? int.Parse(pdf.AsSpan(start, position - start), CultureInfo.InvariantCulture)
            : throw new FormatException("A number was expected.");
    }

    private static bool StartsWith(byte[] pdf, int position, ReadOnlySpan<byte> text) =>
        position >= 0 && position + text.Length <= pdf.Length && pdf.AsSpan(position, text.Length).SequenceEqual(text);

    private static int LastIndexOf(byte[] pdf, ReadOnlySpan<byte> text) => pdf.AsSpan().LastIndexOf(text);

    private static string HeaderVersion(byte[] pdf) =>
        StartsWith(pdf, 0, "%PDF-"u8) && pdf.Length > 8 ? Encoding.ASCII.GetString(pdf, 5, 3) : "1.7";
}
