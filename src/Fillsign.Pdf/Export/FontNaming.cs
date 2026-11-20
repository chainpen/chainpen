using System.Buffers.Binary;
using System.Text;

namespace Fillsign.Pdf.Export;

/// <summary>
/// Font file details the writer needs (FS-612): the subset tag PDF expects on an embedded subset's name
/// (<c>ABCDEF+NotoSans-Regular</c>, ISO 32000 §9.6.4), and whether a font's licence lets it be embedded at all.
/// PDFium names an embedded font after the font's own PostScript name, so the tag is written into the font itself.
/// </summary>
public static class FontNaming
{
    private const uint NameTag = 0x6E616D65; // 'name'
    private const uint HeadTag = 0x68656164; // 'head'
    private const uint Os2Tag = 0x4F532F32; // 'OS/2'
    private const ushort PostScriptNameId = 6;
    private const ushort FullNameId = 4;
    private const ushort FamilyNameId = 1;
    private const ushort SubfamilyNameId = 2;

    /// <summary>A six-letter tag from the characters a subset holds, so the same text gives the same name.</summary>
    public static string SubsetTag(IEnumerable<int> codepoints)
    {
        ArgumentNullException.ThrowIfNull(codepoints);
        uint hash = 2166136261;
        foreach (int codepoint in codepoints.Order())
        {
            hash = (hash ^ (uint)codepoint) * 16777619;
        }

        var tag = new char[6];
        for (int i = 0; i < tag.Length; i++)
        {
            tag[i] = (char)('A' + (hash % 26));
            hash /= 26;
            hash ^= hash << 7;
        }

        return new string(tag);
    }

    /// <summary>
    /// Whether the font's OS/2 table allows embedding it. "Restricted licence" fonts (fsType bit 1, with no looser bit
    /// set) must not be embedded; fonts without an OS/2 table are treated as installable.
    /// </summary>
    public static bool AllowsEmbedding(ReadOnlySpan<byte> font)
    {
        if (FindTable(font, Os2Tag) is not { } os2 || os2.Length < 10)
        {
            return true;
        }

        ushort fsType = BinaryPrimitives.ReadUInt16BigEndian(font.Slice(os2.Offset + 8, 2));
        bool isRestricted = (fsType & 0x000F) == 0x0002;
        return !isRestricted;
    }

    /// <summary>
    /// Returns the font with its names replaced so its PostScript name reads <c>TAG+name</c>. The other tables are
    /// copied unchanged and the table directory and checksums are rebuilt.
    /// </summary>
    public static byte[] WithSubsetTag(ReadOnlySpan<byte> font, string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (font.Length < 12)
        {
            throw new ArgumentException("Not a font file.", nameof(font));
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(font);

        // Apple's own TrueType fonts say 'true'; PDF readers expect the standard 1.0 tag for the same outlines.
        if (version == 0x74727565)
        {
            version = 0x00010000;
        }
        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(font[4..]);
        var tables = new List<(uint Tag, byte[] Data)>(tableCount);
        string postScriptName = "Font";
        for (int i = 0; i < tableCount; i++)
        {
            ReadOnlySpan<byte> record = font.Slice(12 + (i * 16), 16);
            uint tableTag = BinaryPrimitives.ReadUInt32BigEndian(record);
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            byte[] data = font.Slice(offset, length).ToArray();
            if (tableTag == NameTag)
            {
                postScriptName = ReadName(data, PostScriptNameId) ?? postScriptName;
            }

            tables.Add((tableTag, data));
        }

        string family = $"{tag}+{postScriptName}";
        byte[] names = BuildNameTable(family);
        int nameIndex = tables.FindIndex(table => table.Tag == NameTag);
        if (nameIndex >= 0)
        {
            tables[nameIndex] = (NameTag, names);
        }
        else
        {
            tables.Add((NameTag, names));
        }

        return Assemble(version, tables);
    }

    private static (int Offset, int Length)? FindTable(ReadOnlySpan<byte> font, uint wanted)
    {
        if (font.Length < 12)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(font[4..]);
        for (int i = 0; i < count && 12 + ((i + 1) * 16) <= font.Length; i++)
        {
            ReadOnlySpan<byte> record = font.Slice(12 + (i * 16), 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(record) == wanted)
            {
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
                return offset >= 0 && length >= 0 && offset + length <= font.Length ? (offset, length) : null;
            }
        }

        return null;
    }

    /// <summary>The PostScript name from a name table: the Windows Unicode record, else the Mac Roman one.</summary>
    private static string? ReadName(byte[] table, ushort nameId)
    {
        if (table.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int strings = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        string? mac = null;
        for (int i = 0; i < count && 6 + ((i + 1) * 12) <= table.Length; i++)
        {
            ReadOnlySpan<byte> record = table.AsSpan(6 + (i * 12), 12);
            ushort platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            int offset = strings + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (id != nameId || offset + length > table.Length)
            {
                continue;
            }

            if (platform == 3)
            {
                return Encoding.BigEndianUnicode.GetString(table, offset, length);
            }

            if (platform == 1)
            {
                mac = Encoding.ASCII.GetString(table, offset, length);
            }
        }

        return mac;
    }

    /// <summary>A small name table: family, style, full name and PostScript name, for Windows and Mac readers.</summary>
    private static byte[] BuildNameTable(string taggedName)
    {
        (ushort Id, string Text)[] entries =
        [
            (FamilyNameId, taggedName),
            (SubfamilyNameId, "Regular"),
            (FullNameId, taggedName),
            (PostScriptNameId, taggedName),
        ];
        var records = new List<(ushort Platform, ushort Encoding, ushort Language, ushort Id, byte[] Text)>();
        foreach ((ushort id, string text) in entries)
        {
            records.Add((1, 0, 0, id, Encoding.ASCII.GetBytes(text)));
            records.Add((3, 1, 0x409, id, Encoding.BigEndianUnicode.GetBytes(text)));
        }

        // Records sorted by platform, encoding, language and name id, as the format requires.
        records.Sort((a, b) => (a.Platform, a.Encoding, a.Language, a.Id).CompareTo((b.Platform, b.Encoding, b.Language, b.Id)));
        int headerLength = 6 + (records.Count * 12);
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)records.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)headerLength);
        output.Write(header);
        int stringOffset = 0;
        Span<byte> record = stackalloc byte[12];
        foreach (var entry in records)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record, entry.Platform);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], entry.Encoding);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], entry.Language);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], entry.Id);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], (ushort)entry.Text.Length);
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)stringOffset);
            output.Write(record);
            stringOffset += entry.Text.Length;
        }

        foreach (var entry in records)
        {
            output.Write(entry.Text);
        }

        return output.ToArray();
    }

    /// <summary>Writes an sfnt: the table directory sorted by tag, tables 4-byte aligned, checksums and head's adjustment set.</summary>
    private static byte[] Assemble(uint version, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        int count = tables.Count;
        int searchRange = 1, entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }

        searchRange *= 16;
        int offset = 12 + (count * 16);
        var output = new byte[offset + tables.Sum(table => (table.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(output, version);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)((count * 16) - searchRange));
        int headOffset = -1;
        for (int i = 0; i < count; i++)
        {
            (uint tag, byte[] data) = tables[i];
            if (tag == HeadTag && data.Length >= 12)
            {
                // checkSumAdjustment is zero while checksums are worked out.
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
                headOffset = offset;
            }

            Span<byte> record = output.AsSpan(12 + (i * 16), 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            data.CopyTo(output, offset);
            offset += (data.Length + 3) & ~3;
        }

        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(output));
        }

        return output;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            }

            sum += word;
        }

        return sum;
    }
}
