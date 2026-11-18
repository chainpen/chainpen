using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fillsign.App.Platform;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;

namespace Fillsign.App.ViewModels;

/// <summary>
/// FS-704: the recovery copy (FR-8.12). While there are unsaved changes the window writes one every 30 seconds to the
/// app data folder; saving or deliberately discarding the work removes it. "Saved" never refers to it
/// (INTERACTION-PATTERNS §1): the status bar says "Recovery copy kept 21:42".
/// </summary>
public sealed partial class EditorViewModel
{
    public static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);

    private DocumentState? _autosavedState;

    /// <summary>Names this document's recovery copy; a recovered document keeps the id of the copy it came from.</summary>
    public Guid RecoveryId { get; private set; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveryStatus), nameof(HasRecoveryStatus))]
    public partial DateTimeOffset? RecoveryKeptAt { get; private set; }

    public string RecoveryStatus => RecoveryKeptAt is { } kept ? $"Recovery copy kept {kept.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}" : string.Empty;

    public bool HasRecoveryStatus => RecoveryKeptAt is not null;

    /// <summary>Downloaded updates, for the status bar's "Update ready" (🎨 10). An instance property so the view can bind to it.</summary>
#pragma warning disable CA1822 // Compiled bindings need an instance member.
    public AppUpdates Updates => AppUpdates.Instance;
#pragma warning restore CA1822

    /// <summary>
    /// Writes the recovery copy if the work has changed since the last one, or removes it once nothing is unsaved.
    /// Runs the writing off the UI thread.
    /// </summary>
    /// <param name="isForUpdate">The app is about to restart to update; the start screen says so instead of "closed unexpectedly".</param>
    public async Task AutosaveAsync(bool isForUpdate = false)
    {
        if (!HasUnsavedChanges)
        {
            DiscardRecoveryCopy();
            return;
        }

        if (IsLoading || (State.Equals(_autosavedState) && !isForUpdate))
        {
            return;
        }

        DocumentState state = State;
        try
        {
            byte[] bytes = await Task.Run(() => DocumentWriter.WorkingCopy(CleanSource(), SourcePages(), state).Pdf).ConfigureAwait(true);
            DateTimeOffset now = DateTimeOffset.Now;
            RecoveryStore.Write(new RecoveryCopy(RecoveryId, Title, WorkingCopyPath, Source.Path, now, IsForUpdate: isForUpdate), bytes);
            _autosavedState = state;
            RecoveryKeptAt = now;
        }
        catch (InvalidOperationException ex)
        {
            // The next autosave tries again; the work is still open here.
            System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be made. {ex.Message}");
        }
    }

    /// <summary>Removes the recovery copy: the work was saved, or the person chose not to keep it.</summary>
    public void DiscardRecoveryCopy()
    {
        if (_autosavedState is null && RecoveryKeptAt is null)
        {
            return;
        }

        RecoveryStore.Delete(RecoveryId);
        _autosavedState = null;
        RecoveryKeptAt = null;
    }

    /// <summary>
    /// Makes this the recovered document (🎨 2 "Recover changes"): its changes are unsaved, saving goes back to the
    /// working copy it came from, and its recovery copy is replaced as work continues.
    /// </summary>
    public void MarkRecovered(RecoveryCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        RecoveryId = copy.Id;
        WorkingCopyPath = copy.WorkingCopyPath;
        LastSavedAt = null;
        _savedState = null;
        _autosavedState = State;
        RecoveryKeptAt = copy.SavedAt;
        RestoredNotice = $"Your unsaved changes from {FormatWhen(copy.SavedAt)} were recovered. Save to keep them in the file.";
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DocumentStatus));
    }
}
