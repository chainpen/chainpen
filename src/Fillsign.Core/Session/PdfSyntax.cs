using System.Globalization;
using System.Text;

namespace Fillsign.Core.Session;

/// <summary>
/// Just enough PDF syntax to read the last trailer and one stream object (FS-107). Every read is bounds-checked and
/// failures throw <see cref="SessionFormatException"/>; the file is untrusted.
/// </summary>
internal static class PdfSyntax
{
    private const int MaxNesting = 32;

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static void SkipWhitespaceAndComments(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            if (IsWhitespace(data[pos]))
            {
                pos++;
            }
            else if (data[pos] == '%')
            {
                while (pos < data.Length && data[pos] is not (byte)'\n' and not (byte)'\r')
                {
                    pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    public static bool TryKeyword(ReadOnlySpan<byte> data, ref int pos, ReadOnlySpan<byte> keyword)
    {
        SkipWhitespaceAndComments(data, ref pos);
        if (!data[pos..].StartsWith(keyword))
        {
            return false;
        }

        int end = pos + keyword.Length;
        if (end < data.Length && !IsWhitespace(data[end]) && !IsDelimiter(data[end]))
        {
            return false;
        }

        pos = end;
        return true;
    }

    public static void ExpectKeyword(ReadOnlySpan<byte> data, ref int pos, ReadOnlySpan<byte> keyword)
    {
        if (!TryKeyword(data, ref pos, keyword))
        {
            throw new SessionFormatException($"Expected '{Encoding.ASCII.GetString(keyword)}' at {pos}.");
        }
    }

    public static long ReadInteger(ReadOnlySpan<byte> data, ref int pos)
    {
        SkipWhitespaceAndComments(data, ref pos);
        int start = pos;
        while (pos < data.Length && pos - start < 18 && char.IsAsciiDigit((char)data[pos]))
        {
            pos++;
        }

        if (pos == start || (pos < data.Length && char.IsAsciiDigit((char)data[pos])))
        {
            throw new SessionFormatException($"Expected a number at {start}.");
        }

        return long.Parse(data[start..pos], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a dictionary starting at <paramref name="pos"/>. Values are kept as their raw source text so they can
    /// be copied into a new trailer unchanged.
    /// </summary>
    public static Dictionary<string, string> ReadDictionary(ReadOnlySpan<byte> data, ref int pos)
    {
        SkipWhitespaceAndComments(data, ref pos);
        if (!data[pos..].StartsWith("<<"u8))
        {
            throw new SessionFormatException($"Expected a dictionary at {pos}.");
        }

        pos += 2;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhitespaceAndComments(data, ref pos);
            if (pos >= data.Length)
            {
                throw new SessionFormatException("Unterminated dictionary.");
            }

            if (data[pos..].StartsWith(">>"u8))
            {
                pos += 2;
                return entries;
            }

            string key = ReadName(data, ref pos);
            SkipWhitespaceAndComments(data, ref pos);
            int start = pos;
            SkipValue(data, ref pos, 0);
            entries[key] = Encoding.Latin1.GetString(data[start..pos]).Trim();
        }
    }

    /// <summary>Reads "N G R" from a raw value; returns the object number.</summary>
    public static bool TryParseReference(string? raw, out int objectNumber)
    {
        objectNumber = 0;
        string[] parts = raw?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts is [string number, string generation, "R"]
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out objectNumber)
            && int.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    public static bool TryParseInteger(string? raw, out long value) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string ReadName(ReadOnlySpan<byte> data, ref int pos)
    {
        if (data[pos] != '/')
        {
            throw new SessionFormatException($"Expected a name at {pos}.");
        }

        int start = pos++;
        while (pos < data.Length && !IsWhitespace(data[pos]) && !IsDelimiter(data[pos]))
        {
            pos++;
        }

        return Encoding.Latin1.GetString(data[start..pos]);
    }

    private static void SkipValue(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxNesting)
        {
            throw new SessionFormatException("Nested too deeply.");
        }

        SkipWhitespaceAndComments(data, ref pos);
        if (pos >= data.Length)
        {
            throw new SessionFormatException("Unexpected end of file.");
        }

        byte b = data[pos];
        if (data[pos..].StartsWith("<<"u8))
        {
            pos += 2;
            while (true)
            {
                SkipWhitespaceAndComments(data, ref pos);
                if (pos >= data.Length)
                {
                    throw new SessionFormatException("Unterminated dictionary.");
                }

                if (data[pos..].StartsWith(">>"u8))
                {
                    pos += 2;
                    return;
                }

                SkipValue(data, ref pos, depth + 1);
            }
        }

        switch (b)
        {
            case (byte)'[':
                pos++;
                while (true)
                {
                    SkipWhitespaceAndComments(data, ref pos);
                    if (pos >= data.Length)
                    {
                        throw new SessionFormatException("Unterminated array.");
                    }

                    if (data[pos] == ']')
                    {
                        pos++;
                        return;
                    }

                    SkipValue(data, ref pos, depth + 1);
                }

            case (byte)'(':
                SkipLiteralString(data, ref pos);
                return;

            case (byte)'<':
            {
                int end = data[pos..].IndexOf((byte)'>');
                pos = end < 0 ? throw new SessionFormatException("Unterminated hex string.") : pos + end + 1;
                return;
            }

            case (byte)'/':
                ReadName(data, ref pos);
                return;

            case (byte)')' or (byte)'>' or (byte)']' or (byte)'{' or (byte)'}':
                throw new SessionFormatException($"Unexpected '{(char)b}' at {pos}.");
        }

        // A number, keyword (true/false/null) or a reference "N G R".
        SkipToken(data, ref pos);
        int afterFirst = pos;
        if (TrySkipReferenceTail(data, ref pos))
        {
            return;
        }

        pos = afterFirst;
    }

    private static bool TrySkipReferenceTail(ReadOnlySpan<byte> data, ref int pos)
    {
        int probe = pos;
        SkipWhitespaceAndComments(data, ref probe);
        int generationStart = probe;
        while (probe < data.Length && char.IsAsciiDigit((char)data[probe]))
        {
            probe++;
        }

        if (probe == generationStart)
        {
            return false;
        }

        SkipWhitespaceAndComments(data, ref probe);
        if (probe < data.Length && data[probe] == 'R' && (probe + 1 == data.Length || IsWhitespace(data[probe + 1]) || IsDelimiter(data[probe + 1])))
        {
            pos = probe + 1;
            return true;
        }

        return false;
    }

    private static void SkipToken(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && !IsWhitespace(data[pos]) && !IsDelimiter(data[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            throw new SessionFormatException($"Unexpected byte at {pos}.");
        }
    }

    private static void SkipLiteralString(ReadOnlySpan<byte> data, ref int pos)
    {
        int nesting = 0;
        for (; pos < data.Length; pos++)
        {
            switch (data[pos])
            {
                case (byte)'\\':
                    pos++;
                    break;
                case (byte)'(':
                    nesting++;
                    break;
                case (byte)')':
                    nesting--;
                    if (nesting == 0)
                    {
                        pos++;
                        return;
                    }

                    break;
            }
        }

        throw new SessionFormatException("Unterminated string.");
    }
}
