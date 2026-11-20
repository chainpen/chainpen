using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Platform;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Import;

namespace Fillsign.App.ViewModels;

/// <summary>
/// What the export dialog offers (FS-608, 🎨 6): a suggested name and folder, the final copy's size, and possible gaps
/// to look at first (FS-609).
/// </summary>
public sealed record ExportRequest(string SuggestedName, string Folder, long Size, bool ContainsSignature, bool CanSaveWorkingCopy)
{
    public Fillsign.Core.Gaps.GapReport Gaps { get; init; } = Fillsign.Core.Gaps.GapReport.None;

    /// <summary>Pages in the document, so the dialog can say when only some were checked.</summary>
    public int PageCount { get; init; }

    /// <summary>The document has fields to fill (FS-950), so the dialog offers to lock them.</summary>
    public bool HasFormFields { get; init; }
}

/// <summary>What the person chose in the export dialog.</summary>
public abstract record ExportDecision;

/// <summary>Where the person chose to put the final copy, and whether to save the working copy too.</summary>
/// <param name="LockFormFields">Make the form's fields read-only in the final copy, so the answers can't be changed.</param>
public sealed record ExportChoice(string Path, bool AlsoSaveWorkingCopy, bool LockFormFields = false) : ExportDecision;

/// <summary>Show me: look at this possible gap before exporting.</summary>
public sealed record CheckGapChoice(int GapIndex) : ExportDecision;

/// <summary>The green notice after exporting (🎨 8).</summary>
public sealed record ExportedNotice(string FileName, string Folder, string Path, long Size)
{
    /// <summary>Names the folder, not its whole path, so the notice stays on one line; the path is in its tooltip.</summary>
    public string Text => $"{FileName} ({SaveSizes.Describe(Size)}) in the “{FolderName}” folder. This is the one to send.";

    public string FolderName => System.IO.Path.GetFileName(Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    public string Where => System.IO.Path.Combine(SaveSizes.ShortFolder(Folder), FileName);
}

/// <summary>Sizes in words people use: "640 KB", "1.1 MB".</summary>
public static class SaveSizes
{
    /// <summary>"~/Documents/Clinic" rather than the full home path.</summary>
    public static string ShortFolder(string folder)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !OperatingSystem.IsWindows() && home.Length > 0 && folder.StartsWith(home, StringComparison.Ordinal) ? "~" + folder[home.Length..] : folder;
    }

    public static string Describe(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, (int)Math.Round(bytes / 1024.0))} KB"
        : (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// Phase 6: Save keeps the working copy, Export final copy makes the file to send (FR-8, FR-9, INTERACTION-PATTERNS
/// §1). The file on disk is only ever written where the person chose (NFR-8).
/// </summary>
public sealed partial class EditorViewModel
{
    private const string FinalSuffix = "-final";

    /// <summary>Where the working copy is saved, once it has been; Save writes there without asking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(DocumentStatus))]
    public partial string? WorkingCopyPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentStatus))]
    public partial DateTimeOffset? LastSavedAt { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportedNotice))]
    public partial ExportedNotice? Exported { get; private set; }

    public bool HasExportedNotice => Exported is not null;

    /// <summary>"Your edits from 17/11/2026, 21:43 were restored…" (🎨 5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestoredNotice))]
    public partial string? RestoredNotice { get; private set; }

    public bool HasRestoredNotice => RestoredNotice is not null;

    /// <summary>"Open final copy instead" is offered after restoring a working copy, not after recovering work.</summary>
    [ObservableProperty]
    public partial bool CanOpenAsFinalCopy { get; private set; }

    /// <summary>The working copy was changed in another app (D8): what restoring would bring back, until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangedElsewhere), nameof(ChangedElsewhereDetail), nameof(DocumentStatus))]
    public partial WorkingCopyRestore? ChangedElsewhere { get; private set; }

    public bool IsChangedElsewhere => ChangedElsewhere is not null;

    public string ChangedElsewhereDetail => ChangedElsewhere?.SavedAt is { } saved
        ? $"Restoring brings back your editable ticks, text and signature from {FormatWhen(saved)}, but removes the other app’s changes."
        : "Restoring brings back your editable ticks, text and signature, but removes the other app’s changes.";

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>The Save menu's reminder (FS-610, 🎨 5): the working copy can be edited by anyone with Fillsign.</summary>
    public bool ShowsSignatureReminder => State.ContainsSignature;

    /// <summary>Applies a working copy's record once the editor exists (FR-9.2, 9.3, 9.6).</summary>
    public void ApplyRestore(WorkingCopyRestore? restore)
    {
        if (restore is null)
        {
            return;
        }

        if (restore.Kind == RestoreKind.ChangedElsewhere)
        {
            ChangedElsewhere = restore;
            return;
        }

        State = restore.Document;
        _savedState = State;
        WorkingCopyPath = Source.Path;
        LastSavedAt = restore.SavedAt;
        CanOpenAsFinalCopy = Source.Path is not null;
        RestoredNotice = restore.SavedAt is { } saved
            ? $"Your edits from {FormatWhen(saved)} were restored. You can move, change or delete anything you added."
            : "Your edits were restored. You can move, change or delete anything you added.";
        RebuildPages();
        ItemsChanged?.Invoke();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DocumentStatus));
    }

    /// <summary>
    /// Brings back the Fillsign edits after the file was changed elsewhere, discarding the other app's changes (FR-9.6).
    /// The edits count as unsaved, since the file on disk still holds the other app's version.
    /// </summary>
    public void RestoreEditsOver(WorkingCopyRestore restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        State = restore.Document;
        _savedState = null;
        WorkingCopyPath = Source.Path;
        RebuildPages();
        ItemsChanged?.Invoke();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DocumentStatus));
    }

    /// <summary>
    /// Print… (Mod+P, FS-808): prints the document as its final copy would look, through the system. The copy goes in
    /// a private folder and is cleared after a day.
    /// </summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        FinishTyping();
        CancelGesture();
        if (IsLoading)
        {
            await _host.ShowMessageAsync("Print", $"Available when all {Source.PageCount} pages have loaded. {LoadingText}.").ConfigureAwait(true);
            return;
        }

        WrittenDocument final;
        IsSaving = true;
        try
        {
            DocumentState state = State;
            final = await Task.Run(() => DocumentWriter.FinalCopy(CleanSource(), SourcePages(), state)).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            await ShowSaveProblemAsync("The document couldn’t be printed", ex).ConfigureAwait(true);
            return;
        }
        finally
        {
            IsSaving = false;
        }

        string path;
        try
        {
            Printing.ClearOldCopies(DateTime.UtcNow);
            byte[] toPrint = DocumentWriter.ForPrinting(final.Pdf);
            path = await Task.Run(() => Printing.WriteCopy(Title, DateTime.Now, toPrint)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"The print copy couldn't be written: {ex}");
            await _host.ShowMessageAsync("The document couldn’t be printed", "Fillsign couldn’t make a copy to print. Export a final copy and print it from your PDF viewer instead.").ConfigureAwait(true);
            return;
        }

        switch (_host.PrintFile(path))
        {
            case PrintOutcome.OpenedInViewer:
                await _host.ShowInformationAsync("Print from your PDF viewer", "The document opened in your PDF viewer, looking as it will print. Print it from there.").ConfigureAwait(true);
                break;
            case PrintOutcome.Failed:
                await _host.ShowMessageAsync("Printing couldn’t start", "Export a final copy and print it from your PDF viewer instead.").ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// View only (FS-214): saving or exporting would make a copy without the owner's restriction, so it waits until
    /// the person chooses Edit anyway. Returns true when it refused.
    /// </summary>
    private async Task<bool> RefuseWhileViewOnlyAsync()
    {
        if (!IsViewOnly)
        {
            return false;
        }

        await _host.ShowMessageAsync("This document is open to view only", "Its owner set it so it shouldn’t be changed. Choose Edit anyway in the bar above the page to fill it in, save or export it.").ConfigureAwait(true);
        return true;
    }

    /// <summary>Save (Mod+S): writes the working copy where it was saved before, or asks where the first time (FR-8.1).</summary>
    [RelayCommand]
    public async Task<bool> SaveAsync() => WorkingCopyPath is { } path ? await SaveWorkingCopyToAsync(path).ConfigureAwait(true) : await SaveAsAsync().ConfigureAwait(true);

    /// <summary>Save as… (Mod+Shift+S): a working copy with a new name or in another folder.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsAsync()
    {
        if (await RefuseWhileViewOnlyAsync().ConfigureAwait(true))
        {
            return false;
        }

        FinishTyping();
        string? path = await _host.AskWorkingCopyPathAsync(WorkingCopyName(), SuggestedFolder()).ConfigureAwait(true);
        return path is not null && await SaveWorkingCopyToAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Export final copy… (Mod+E, FR-8.2): makes the file to send, never containing the editable record (FR-9.4), and
    /// optionally saves the working copy too.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        FinishTyping();
        CancelGesture();
        if (await RefuseWhileViewOnlyAsync().ConfigureAwait(true))
        {
            return;
        }

        if (IsLoading)
        {
            // INTERACTION-PATTERNS §6: say why rather than disabling the button.
            await _host.ShowMessageAsync("Export final copy", $"Available when all {Source.PageCount} pages have loaded. {LoadingText}.").ConfigureAwait(true);
            return;
        }

        StopChecking();
        WrittenDocument final;
        Fillsign.Core.Gaps.GapReport gaps;
        IsSaving = true;
        try
        {
            DocumentState state = State;
            (final, gaps) = await Task.Run(() => (DocumentWriter.FinalCopy(CleanSource(), SourcePages(), state), FindGaps(state))).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            await ShowSaveProblemAsync("The final copy couldn’t be made", ex).ConfigureAwait(true);
            return;
        }
        finally
        {
            IsSaving = false;
        }

        var request = new ExportRequest(FinalName(), SuggestedFolder(), final.Pdf.Length, State.ContainsSignature, CanSaveWorkingCopy: true)
        {
            Gaps = gaps,
            PageCount = State.Pages.Length,
            HasFormFields = HasFormFields(),
        };
        ExportDecision? decision = await _host.AskExportAsync(request).ConfigureAwait(true);
        if (decision is CheckGapChoice check)
        {
            StartChecking(gaps, check.GapIndex);
            return;
        }

        if (decision is not ExportChoice choice)
        {
            return;
        }

        byte[] finalPdf = final.Pdf;
        if (choice.LockFormFields)
        {
            finalPdf = await Task.Run(() => DocumentWriter.WithFormFieldsLocked(final.Pdf)).ConfigureAwait(true);
        }

        if (!await WriteFileAsync(choice.Path, finalPdf).ConfigureAwait(true))
        {
            return;
        }

        if (choice.AlsoSaveWorkingCopy && HasUnsavedChanges)
        {
            await SaveAsync().ConfigureAwait(true);
        }

        AppSettings.Update(settings => settings.WithRecent(choice.Path, DateTimeOffset.Now, isFinalCopy: true));
        Exported = new ExportedNotice(Path.GetFileName(choice.Path), Path.GetDirectoryName(choice.Path) ?? string.Empty, choice.Path, finalPdf.Length);
        if (final.HaveFormsMoved)
        {
            await _host.ShowMessageAsync("Fillable fields may not work", FormsMovedMessage).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ShowExportedInFolder()
    {
        if (Exported is { } exported)
        {
            _host.ShowInFolder(exported.Path);
        }
    }

    [RelayCommand]
    private void DismissExported() => Exported = null;

    [RelayCommand]
    private void DismissRestored() => RestoredNotice = null;

    /// <summary>"Open final copy instead" (🎨 5): shows the pages as recipients see them; the file is unchanged.</summary>
    [RelayCommand]
    private async Task OpenAsFinalCopyAsync()
    {
        if (Source.Path is { } path)
        {
            await _host.OpenAsFinalCopyAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>"Restore my Fillsign edits…" (D8): asks first, because it removes the other app's changes.</summary>
    [RelayCommand]
    private async Task RestoreChangedEditsAsync()
    {
        if (ChangedElsewhere is not { } restore || !await _host.ConfirmRestoreAsync(restore.SavedAt).ConfigureAwait(true))
        {
            return;
        }

        ChangedElsewhere = null;
        await _host.RestoreEditsAsync(restore).ConfigureAwait(true);
    }

    [RelayCommand]
    private void KeepAsItIs() => ChangedElsewhere = null;

    private async Task<bool> SaveWorkingCopyToAsync(string path)
    {
        if (await RefuseWhileViewOnlyAsync().ConfigureAwait(true))
        {
            return false;
        }

        FinishTyping();
        DocumentState saving = State;
        WrittenDocument written;
        IsSaving = true;
        try
        {
            written = await Task.Run(() => DocumentWriter.WorkingCopy(CleanSource(), SourcePages(), saving)).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            await ShowSaveProblemAsync("Your working copy couldn’t be saved", ex).ConfigureAwait(true);
            return false;
        }
        finally
        {
            IsSaving = false;
        }

        if (!await WriteFileAsync(path, written.Pdf).ConfigureAwait(true))
        {
            return false;
        }

        _savedState = saving;
        WorkingCopyPath = path;
        LastSavedAt = DateTimeOffset.Now;
        if (!HasUnsavedChanges)
        {
            // Saved to the file, so there's nothing left to recover.
            DiscardRecoveryCopy();
        }

        AppSettings.Update(settings => settings.WithRecent(path, DateTimeOffset.Now));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DocumentStatus));
        if (written.HaveFormsMoved)
        {
            await _host.ShowMessageAsync("Fillable fields may not work", FormsMovedMessage).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>Writes next to the target and swaps it in, so a failed save never leaves half a file (NFR-8).</summary>
    private async Task<bool> WriteFileAsync(string path, byte[] contents)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path) ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, contents).ConfigureAwait(true);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            await _host.ShowMessageAsync(
                $"{Path.GetFileName(path)} couldn’t be saved",
                $"Fillsign doesn’t have permission to write there, or the disk is full or the file is open in another app. Try another folder. ({ex.Message})").ConfigureAwait(true);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"A temporary file could not be removed. {ex.Message}");
        }
    }

    private Task ShowSaveProblemAsync(string title, InvalidOperationException ex)
    {
        System.Diagnostics.Trace.TraceError($"{title}: {ex}");
        return _host.ShowMessageAsync(title, "Fillsign couldn’t write this document. Your edits are still open here, so nothing is lost. Try again, or export the pages as they are.");
    }

    private const string FormsMovedMessage =
        "This document’s pages couldn’t be reordered in place, so they were copied in their new order. Fillable fields and links may not work in the saved file.";

    /// <summary>The clean source: every opened and added page, unencrypted, with nothing drawn in.</summary>
    /// <summary>
    /// The document as Fillsign holds it, compacted (FS-967): the working copy is this plus an update, so it starts as
    /// small as the original rather than as PDFium writes it.
    /// </summary>
    private byte[] CleanSource() => PdfCompactor.Compact(Source.Pdf.SaveCopy(removeSecurity: true));

    private SourcePage[] SourcePages()
    {
        Source.LoadPages(null, CancellationToken.None);
        return [.. Enumerable.Range(0, Source.PageCount).Select(Source.GetPage)];
    }

    /// <summary>The working copy keeps the document's name (🎨 6: "Keeps your edits changeable in consent-form.pdf").</summary>
    private string WorkingCopyName() => Path.GetFileName(WorkingCopyPath) ?? PdfName(Source.Name);

    private string FinalName() => Path.GetFileNameWithoutExtension(WorkingCopyName()) + FinalSuffix + ".pdf";

    private string SuggestedFolder() =>
        Path.GetDirectoryName(WorkingCopyPath ?? Source.Path) is { Length: > 0 } folder
            ? folder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>"photo.jpg" becomes "photo.pdf"; a PDF keeps its name.</summary>
    private static string PdfName(string name) =>
        string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase) ? name : Path.GetFileNameWithoutExtension(name) + ".pdf";

    private static string FormatWhen(DateTimeOffset when) => when.ToLocalTime().ToString("dd/MM/yyyy, HH:mm", CultureInfo.InvariantCulture);
}
