using Fillsign.Pdf.Localization;

namespace Fillsign.Pdf.Import;

public enum OpenProblemKind
{
    /// <summary>PDFium found nothing it can read.</summary>
    Damaged,

    /// <summary>Encryption PDFium doesn’t support (e.g. certificate security).</summary>
    UnsupportedProtection,

    /// <summary>Neither a PDF nor an image Fillsign can decode.</summary>
    NotPdfOrImage,

    /// <summary>The file couldn’t be read from disk (permissions, missing, in use).</summary>
    CantRead,
}

/// <summary>Why one file couldn’t be opened, worded for the person (FR-1.6: what happened, why, what to do).</summary>
public sealed record OpenProblem(string FileName, OpenProblemKind Kind)
{
    public string Title => Format(Kind == OpenProblemKind.Damaged ? Strings.Open_DamagedTitle : Strings.Open_CantOpenTitle);

    public string Message => Kind switch
    {
        OpenProblemKind.Damaged => Strings.Open_DamagedMessage,
        OpenProblemKind.UnsupportedProtection => Format(Strings.Open_UnsupportedProtectionMessage),
        OpenProblemKind.NotPdfOrImage => Format(Strings.Open_NotPdfOrImageMessage),
        _ => Format(Strings.Open_CantReadMessage),
    };

    private string Format(string template) => Text.Format(template, FileName);
}

/// <summary>None of the chosen files could be opened.</summary>
public sealed class DocumentOpenException(IReadOnlyList<OpenProblem> problems)
    : Exception(problems.Count > 0 ? problems[0].Title : Strings.Open_NothingOpened)
{
    public IReadOnlyList<OpenProblem> Problems { get; } = problems;
}
