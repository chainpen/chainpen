namespace Fillsign.Pdf.Import;

public enum OpenProblemKind
{
    /// <summary>PDFium found nothing it can read.</summary>
    Damaged,

    /// <summary>Encryption PDFium doesn't support (e.g. certificate security).</summary>
    UnsupportedProtection,

    /// <summary>Neither a PDF nor an image Fillsign can decode.</summary>
    NotPdfOrImage,

    /// <summary>The file couldn't be read from disk (permissions, missing, in use).</summary>
    CantRead,
}

/// <summary>Why one file couldn't be opened, worded for the person (FR-1.6: what happened, why, what to do).</summary>
public sealed record OpenProblem(string FileName, OpenProblemKind Kind)
{
    public string Title => Kind switch
    {
        OpenProblemKind.Damaged => $"{FileName} is damaged and can't be opened",
        OpenProblemKind.UnsupportedProtection => $"{FileName} can't be opened",
        OpenProblemKind.NotPdfOrImage => $"{FileName} can't be opened",
        _ => $"{FileName} can't be opened",
    };

    public string Message => Kind switch
    {
        OpenProblemKind.Damaged =>
            "Fillsign couldn't find any pages it can read in this file. If someone sent it to you, ask them to send it again.",
        OpenProblemKind.UnsupportedProtection =>
            $"{FileName} uses a kind of protection Fillsign can't open. Open it in the app that came with it, save an unprotected copy, then open that copy here.",
        OpenProblemKind.NotPdfOrImage =>
            $"{FileName} isn't a PDF or an image. Save it as a PDF first, then open it here.",
        _ =>
            $"Fillsign doesn't have permission to read {FileName}, or it's no longer there. Move it to your Documents folder and try again.",
    };
}

/// <summary>None of the chosen files could be opened.</summary>
public sealed class DocumentOpenException(IReadOnlyList<OpenProblem> problems)
    : Exception(problems.Count > 0 ? problems[0].Title : "Nothing could be opened.")
{
    public IReadOnlyList<OpenProblem> Problems { get; } = problems;
}
