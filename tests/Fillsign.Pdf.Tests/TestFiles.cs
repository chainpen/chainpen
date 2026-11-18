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
}
