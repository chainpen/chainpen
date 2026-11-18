using Fillsign.Core.Model;
using Fillsign.Pdf.Import;

namespace Fillsign.App.ViewModels;

/// <summary>What the person chose when opening a file while a document is open (D5, E9).</summary>
public enum OpenChoice
{
    Cancel,
    NewWindow,
    AddPages,
}

/// <summary>Window services the view models need: pickers, dialogs and new windows.</summary>
public interface IEditorHost
{
    /// <summary>Shows the file picker for PDFs and images. Empty when cancelled.</summary>
    Task<IReadOnlyList<string>> ChooseFilesAsync();

    /// <summary>D12: asks for a file's password. Null skips the file.</summary>
    Task<string?> AskPasswordAsync(string fileName, bool wasWrong);

    /// <summary>D5: open in a new window, or add the pages to this document.</summary>
    Task<OpenChoice> AskOpenOrAddAsync(string title, string documentName, int pageCount);

    /// <summary>D6: explains files that couldn't be opened.</summary>
    Task ShowProblemsAsync(IReadOnlyList<OpenProblem> problems, int openedCount);

    /// <summary>Explains why something isn't allowed (D9).</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>FR-3.4: picks or makes a signature or initials to place. Null when cancelled.</summary>
    Task<(SignatureImage Image, SignatureKind Kind)?> ChooseSignatureAsync();

    /// <summary>Opens files in a separate window, leaving this document unchanged (FR-1.5).</summary>
    void OpenInNewWindow(IReadOnlyList<string> paths);
}
