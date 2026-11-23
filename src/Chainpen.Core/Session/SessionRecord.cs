using System.Security.Cryptography;
using Chainpen.Core.Model;

namespace Chainpen.Core.Session;

/// <summary>
/// The editable record stored inside a working copy (FR-9.1): the page list and every item, plus fingerprints that
/// show whether the file was changed by another app since Chainpen saved it (FR-9.6). Format in docs/FILE-FORMAT.md.
/// </summary>
/// <param name="Base">The clean original: the first <see cref="ByteStamp.Length"/> bytes of the file (FR-9.2).</param>
/// <param name="Content">The file as Chainpen saved it, up to where the session update starts.</param>
/// <param name="SourcePageCount">Pages in the clean original, so page references can be checked before use.</param>
public sealed record SessionRecord(ByteStamp Base, ByteStamp Content, int SourcePageCount, DocumentState Document)
{
    public const int CurrentVersion = 1;
}

/// <summary>The length and SHA-256 of a prefix of a file.</summary>
public readonly record struct ByteStamp(long Length, string Sha256)
{
    public static ByteStamp Of(ReadOnlySpan<byte> bytes) => new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>Whether <paramref name="file"/> starts with exactly the bytes this stamp describes.</summary>
    public bool MatchesPrefixOf(ReadOnlySpan<byte> file) =>
        Length > 0 && Length <= file.Length && Of(file[..(int)Length]).Sha256 == Sha256;
}
