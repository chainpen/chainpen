using System.Globalization;
using System.IO.Compression;
using System.Text;
using Chainpen.Core.Model;

namespace Chainpen.Core.Session;

public enum SessionStatus
{
    /// <summary>A plain PDF: no Chainpen record.</summary>
    None,

    /// <summary>The file is exactly as Chainpen saved it; the record can be restored (FR-9.2).</summary>
    Intact,

    /// <summary>
    /// Another app changed the file after Chainpen saved it. Open it as it is now; restoring is a separate,
    /// deliberate choice that discards the other app's changes (FR-9.6).
    /// </summary>
    ChangedElsewhere,

    /// <summary>A record is referenced but can't be read. Open as a normal PDF (FR-9.5).</summary>
    Damaged,
}

/// <param name="IsOriginalAvailable">Whether the clean original is still at the start of the file, so a restore can
/// show the clean pages.</param>
public sealed record SessionLookup(SessionStatus Status, SessionRecord? Record, bool IsOriginalAvailable, string? Problem = null)
{
    public static readonly SessionLookup NotFound = new(SessionStatus.None, null, false);
}

/// <summary>
/// Stores the editable record in a working copy and finds it again. The record goes in our own incremental update:
/// one Flate-compressed stream object, referenced from the trailer by a private <c>/ChainpenSession</c> key. Other
/// readers ignore the key, so they show the finished pages (FR-9.1).
/// </summary>
public static class SessionStore
{
    public const string TrailerKey = "/ChainpenSession";

    // startxref sits in the last few hundred bytes; allow for trailing junk some writers leave.
    private const int TailSearchBytes = 4096;

    /// <summary>
    /// Appends the record to <paramref name="pdf"/> as an incremental update. The record's content fingerprint covers
    /// every byte of <paramref name="pdf"/>.
    /// </summary>
    public static byte[] Append(ReadOnlySpan<byte> pdf, ByteStamp original, int sourcePageCount, DocumentState document)
    {
        int startXref = FindStartXref(pdf) ?? throw new SessionFormatException("The PDF has no startxref.");
        Section previous = ReadSection(pdf, startXref);
        Dictionary<string, string> trailer = previous.Trailer;
        if (!PdfSyntax.TryParseInteger(trailer.GetValueOrDefault("/Size"), out long size) || size < 1 || size >= int.MaxValue)
        {
            throw new SessionFormatException("The trailer has no usable /Size.");
        }

        if (!trailer.TryGetValue("/Root", out string? root))
        {
            throw new SessionFormatException("The trailer has no /Root.");
        }

        var record = new SessionRecord(original, ByteStamp.Of(pdf), sourcePageCount, document);
        byte[] compressed = Compress(SessionJson.Write(record));

        using var output = new MemoryStream(pdf.Length + compressed.Length + 512);
        output.Write(pdf);
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        Write("\n");
        long objectOffset = output.Position;
        Write(Invariant($"{size} 0 obj\n<</Type/ChainpenSession/Filter/FlateDecode/Length {compressed.Length}>>\nstream\n"));
        output.Write(compressed);
        Write("\nendstream\nendobj\n");

        long xrefOffset = output.Position;
        var kept = new StringBuilder();
        foreach (string key in (string[])["/Info", "/ID", "/Encrypt"])
        {
            if (trailer.TryGetValue(key, out string? value))
            {
                kept.Append(Invariant($" {key} {value}"));
            }
        }

        if (!previous.IsStream)
        {
            // Each cross-reference entry is exactly 20 bytes, including its two-byte line end.
            Write(Invariant($"xref\n{size} 1\n{objectOffset:D10} 00000 n\r\ntrailer\n<</Size {size + 1} /Root {root}{kept} /Prev {startXref} {TrailerKey} {size} 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n"));
            return output.ToArray();
        }

        // Over a cross-reference stream the update needs one too: a classic table can't point back to a stream, and
        // stricter readers (macOS PDFKit) reject the file. Rows of type (1), 4-byte offset, 2-byte generation.
        if (xrefOffset > uint.MaxValue)
        {
            throw new SessionFormatException("The file is too large for the record's cross-reference stream.");
        }

        byte[] rows = [1, .. BigEndian((uint)objectOffset), 0, 0, 1, .. BigEndian((uint)xrefOffset), 0, 0];
        Write(Invariant($"{size + 1} 0 obj\n<</Type/XRef/Size {size + 2} /W[1 4 2] /Index[{size} 2] /Root {root}{kept} /Prev {startXref} {TrailerKey} {size} 0 R/Length {rows.Length}>>\nstream\n"));
        output.Write(rows);
        Write(Invariant($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>Looks for a Chainpen record and checks whether the file is still as Chainpen saved it.</summary>
    public static SessionLookup Find(ReadOnlySpan<byte> pdf)
    {
        try
        {
            return FindCore(pdf);
        }
        catch (SessionFormatException ex)
        {
            return new SessionLookup(SessionStatus.Damaged, null, false, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new SessionLookup(SessionStatus.Damaged, null, false, "The record's compressed data is damaged: " + ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // A length or offset in the file points outside it.
            return new SessionLookup(SessionStatus.Damaged, null, false, "An offset points outside the file: " + ex.Message);
        }
    }

    /// <summary>
    /// The file without Chainpen's record update, so saving a restored document again doesn't grow it (FR-9.8).
    /// Returns the whole file when the last update isn't Chainpen's.
    /// </summary>
    public static ReadOnlySpan<byte> WithoutRecord(ReadOnlySpan<byte> pdf)
    {
        SessionLookup lookup = Find(pdf);
        return lookup is { Status: SessionStatus.Intact, Record: { } record } ? pdf[..(int)record.Content.Length] : pdf;
    }

    private static SessionLookup FindCore(ReadOnlySpan<byte> pdf)
    {
        if (FindStartXref(pdf) is not int startXref)
        {
            return SessionLookup.NotFound;
        }

        Section section = ReadSection(pdf, startXref);
        if (!section.Trailer.TryGetValue(TrailerKey, out string? reference))
        {
            return SessionLookup.NotFound;
        }

        if (!PdfSyntax.TryParseReference(reference, out int objectNumber))
        {
            throw new SessionFormatException("The record reference is badly formed.");
        }

        int objectOffset = section.Offsets.TryGetValue(objectNumber, out int known) ? known : FindObject(pdf, objectNumber);
        SessionRecord record = SessionJson.Read(ReadStream(pdf, objectOffset, objectNumber));

        bool isOriginalAvailable = record.Base.MatchesPrefixOf(pdf);
        long contentEnd = record.Content.Length;
        bool isOnlyUpdateAfterContent =
            record.Content.MatchesPrefixOf(pdf)
            && startXref >= contentEnd
            && objectOffset >= contentEnd
            && CountOccurrences(pdf[(int)contentEnd..], "startxref"u8) == 1;
        SessionStatus status = isOnlyUpdateAfterContent && isOriginalAvailable ? SessionStatus.Intact : SessionStatus.ChangedElsewhere;
        return new SessionLookup(status, record, isOriginalAvailable);
    }

    private static int? FindStartXref(ReadOnlySpan<byte> pdf)
    {
        int tailStart = Math.Max(0, pdf.Length - TailSearchBytes);
        int index = pdf[tailStart..].LastIndexOf("startxref"u8);
        if (index < 0)
        {
            return null;
        }

        int pos = tailStart + index + "startxref".Length;
        long offset = PdfSyntax.ReadInteger(pdf, ref pos);
        return offset < pdf.Length ? (int)offset : throw new SessionFormatException("startxref points past the end of the file.");
    }

    /// <summary>Reads the cross-reference section at an offset: a classic table and trailer, or an xref stream.</summary>
    private static Section ReadSection(ReadOnlySpan<byte> pdf, int offset)
    {
        int pos = offset;
        var offsets = new Dictionary<int, int>();
        if (PdfSyntax.TryKeyword(pdf, ref pos, "xref"u8))
        {
            while (!PdfSyntax.TryKeyword(pdf, ref pos, "trailer"u8))
            {
                long first = PdfSyntax.ReadInteger(pdf, ref pos);
                long count = PdfSyntax.ReadInteger(pdf, ref pos);
                if (count > pdf.Length / 20)
                {
                    throw new SessionFormatException("The cross-reference table is longer than the file.");
                }

                for (long i = 0; i < count; i++)
                {
                    long entryOffset = PdfSyntax.ReadInteger(pdf, ref pos);
                    PdfSyntax.ReadInteger(pdf, ref pos);
                    PdfSyntax.SkipWhitespaceAndComments(pdf, ref pos);
                    bool isInUse = pos < pdf.Length && pdf[pos] == 'n';
                    pos++;
                    if (isInUse && entryOffset < pdf.Length && first + i < int.MaxValue)
                    {
                        offsets[(int)(first + i)] = (int)entryOffset;
                    }
                }
            }

            return new Section(PdfSyntax.ReadDictionary(pdf, ref pos), offsets, IsStream: false);
        }

        // An xref stream: "N G obj <<…>> stream". Only its dictionary is needed.
        PdfSyntax.ReadInteger(pdf, ref pos);
        PdfSyntax.ReadInteger(pdf, ref pos);
        PdfSyntax.ExpectKeyword(pdf, ref pos, "obj"u8);
        return new Section(PdfSyntax.ReadDictionary(pdf, ref pos), offsets, IsStream: true);
    }

    /// <summary>Fallback when the object isn't in the last table: the last "N 0 obj" in the file.</summary>
    private static int FindObject(ReadOnlySpan<byte> pdf, int objectNumber)
    {
        byte[] header = Encoding.ASCII.GetBytes(Invariant($"{objectNumber} 0 obj"));
        ReadOnlySpan<byte> search = pdf;
        while (true)
        {
            int index = search.LastIndexOf(header);
            if (index < 0)
            {
                throw new SessionFormatException("The record object is missing.");
            }

            if (index == 0 || PdfSyntax.IsWhitespace(pdf[index - 1]))
            {
                return index;
            }

            search = search[..index];
        }
    }

    private static byte[] ReadStream(ReadOnlySpan<byte> pdf, int offset, int objectNumber)
    {
        int pos = offset;
        if (PdfSyntax.ReadInteger(pdf, ref pos) != objectNumber)
        {
            throw new SessionFormatException("The record reference points at another object.");
        }

        PdfSyntax.ReadInteger(pdf, ref pos);
        PdfSyntax.ExpectKeyword(pdf, ref pos, "obj"u8);
        Dictionary<string, string> dictionary = PdfSyntax.ReadDictionary(pdf, ref pos);
        PdfSyntax.ExpectKeyword(pdf, ref pos, "stream"u8);

        // The keyword is followed by CRLF or LF, then the data.
        if (pos < pdf.Length && pdf[pos] == '\r')
        {
            pos++;
        }

        if (pos >= pdf.Length || pdf[pos] != '\n')
        {
            throw new SessionFormatException("The record stream is badly formed.");
        }

        pos++;
        if (!PdfSyntax.TryParseInteger(dictionary.GetValueOrDefault("/Length"), out long length) || length > pdf.Length - pos)
        {
            throw new SessionFormatException("The record stream's length is missing or wrong.");
        }

        ReadOnlySpan<byte> data = pdf.Slice(pos, (int)length);
        int end = pos + (int)length;
        if (!PdfSyntax.TryKeyword(pdf, ref end, "endstream"u8))
        {
            throw new SessionFormatException("The record stream's length is wrong.");
        }

        string? filter = dictionary.GetValueOrDefault("/Filter");
        return filter switch
        {
            null => data.ToArray(),
            "/FlateDecode" or "[/FlateDecode]" => Decompress(data),
            _ => throw new SessionFormatException($"The record uses an unsupported filter {filter}."),
        };
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > SessionJson.MaxRecordBytes)
            {
                throw new SessionFormatException("The record is too large.");
            }
        }

        return output.ToArray();
    }

    private static int CountOccurrences(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        int count = 0;
        int index;
        while ((index = data.IndexOf(value)) >= 0)
        {
            count++;
            data = data[(index + value.Length)..];
        }

        return count;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record Section(Dictionary<string, string> Trailer, Dictionary<int, int> Offsets, bool IsStream);
}
