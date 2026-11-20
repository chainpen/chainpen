namespace Fillsign.Pdf.Tests;

internal static class TestFiles
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Samples => Path.Combine(RepoRoot, "samples");

    public static string Corpus => Path.Combine(Samples, "corpus");

    public static string Sample(string name) => Path.Combine(Samples, name);

    /// <summary>The corpus is downloaded by build/fetch-corpus.sh and not committed.</summary>
    public static IReadOnlyList<string> CorpusFiles() =>
        Directory.Exists(Corpus) ? [.. Directory.GetFiles(Corpus, "*.pdf").Order(StringComparer.Ordinal)] : [];

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Fillsign.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Fillsign.slnx).");
    }

    /// <summary>
    /// A PDF's text with its compressed object streams unpacked (FS-967 packs the dictionaries into them), so tests
    /// can look for keys such as /AcroForm or /SMask as they could in an unpacked file.
    /// </summary>
    public static string ReadableText(byte[] pdf)
    {
        string raw = System.Text.Encoding.Latin1.GetString(pdf);
        var text = new System.Text.StringBuilder(raw);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(raw, @"/Type /ObjStm [^>]*/Length (\d+) >>\nstream\n"))
        {
            int start = match.Index + match.Length;
            int length = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, length), System.IO.Compression.CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            zlib.CopyTo(unpacked);
            text.Append('\n').Append(System.Text.Encoding.Latin1.GetString(unpacked.ToArray()));
        }

        return text.ToString();
    }
}
