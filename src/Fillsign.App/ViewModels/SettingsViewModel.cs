using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Platform;
using Fillsign.App.Platform.Signatures;
using Fillsign.Core.Model;

namespace Fillsign.App.ViewModels;

/// <summary>
/// Settings (FS-706, 🎨 7, FR-10.1): crash reports, saved signatures and initials, recovery copies and appearance.
/// Every change takes effect and is saved as soon as it's made.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SavedSignatureStore _signatures;
    private readonly Func<string, string, string, Task<bool>> _confirm;

    /// <param name="confirm">Asks before something that can't be undone: (title, body, red button label) → yes.</param>
    public SettingsViewModel(SavedSignatureStore signatures, Func<string, string, string, Task<bool>> confirm)
    {
        _signatures = signatures;
        _confirm = confirm;
        AppSettings settings = AppSettings.Load();
        _appearance = settings.Appearance;
        _recoveryDays = settings.RecoveryDays;
        _sendCrashReports = settings.SendCrashReports == true;
        _singleKeyToolShortcuts = settings.UseSingleKeyTools;
    }

    // ----- Keyboard (FS-951) -----

    private bool _singleKeyToolShortcuts;

    /// <summary>Single letters pick tools (V, T, C…). Off unless turned on.</summary>
    public bool UseSingleKeyTools
    {
        get => _singleKeyToolShortcuts;
        set
        {
            if (SetProperty(ref _singleKeyToolShortcuts, value))
            {
                AppSettings.Update(settings => settings with { UseSingleKeyTools = value });
            }
        }
    }

    // ----- Appearance -----

    private AppearanceChoice _appearance;

    public bool IsMatchSystem
    {
        get => _appearance == AppearanceChoice.MatchSystem;
        set => SetAppearance(value, AppearanceChoice.MatchSystem);
    }

    public bool IsLight
    {
        get => _appearance == AppearanceChoice.Light;
        set => SetAppearance(value, AppearanceChoice.Light);
    }

    public bool IsDark
    {
        get => _appearance == AppearanceChoice.Dark;
        set => SetAppearance(value, AppearanceChoice.Dark);
    }

    private void SetAppearance(bool isChosen, AppearanceChoice choice)
    {
        if (!isChosen || _appearance == choice)
        {
            return;
        }

        _appearance = choice;
        AppSettings.Update(settings => settings with { Appearance = choice });
        OnPropertyChanged(nameof(IsMatchSystem));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
    }

    // ----- Recovery copies -----

    private int _recoveryDays;

    public bool IsOneDay
    {
        get => _recoveryDays == 1;
        set => SetRecoveryDays(value, 1);
    }

    public bool IsSevenDays
    {
        get => _recoveryDays == 7;
        set => SetRecoveryDays(value, 7);
    }

    public bool IsThirtyDays
    {
        get => _recoveryDays == 30;
        set => SetRecoveryDays(value, 30);
    }

    private void SetRecoveryDays(bool isChosen, int days)
    {
        if (!isChosen || _recoveryDays == days)
        {
            return;
        }

        _recoveryDays = days;
        AppSettings.Update(settings => settings with { RecoveryDays = days });
        RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(days), DateTimeOffset.Now);
        OnPropertyChanged(nameof(IsOneDay));
        OnPropertyChanged(nameof(IsSevenDays));
        OnPropertyChanged(nameof(IsThirtyDays));
    }

    /// <summary>"Delete all recovery copies…": can't be undone, so asks first (FR-10.4).</summary>
    [RelayCommand]
    private async Task DeleteRecoveryCopiesAsync()
    {
        int count = RecoveryStore.List().Count;
        if (count == 0)
        {
            Message = "There are no recovery copies to delete.";
            return;
        }

        string what = count == 1 ? "the recovery copy" : $"all {count} recovery copies";
        if (await _confirm(
                "Delete all recovery copies?",
                $"Unsaved changes in {what} will be removed from this computer and can’t be brought back. Documents you’ve saved aren’t affected, and documents open now keep a fresh copy while they’re open.",
                "Delete recovery copies").ConfigureAwait(true))
        {
            RecoveryStore.DeleteAll();
            Message = "Recovery copies deleted.";
        }
    }

    // ----- Crash reports -----

    private bool _sendCrashReports;

    /// <summary>Off unless chosen (NFR-2, FS-016). Reports are kept locally and sent only by the person (FS-902).</summary>
    public bool SendCrashReports
    {
        get => _sendCrashReports;
        set
        {
            if (SetProperty(ref _sendCrashReports, value))
            {
                AppSettings.Update(settings => settings with { SendCrashReports = value });
                OnPropertyChanged(nameof(DontSendCrashReports));
            }
        }
    }

    public bool DontSendCrashReports
    {
        get => !_sendCrashReports;
        set => SendCrashReports = !value;
    }

    // ----- Saved signatures (FR-4.7) -----

    public ObservableCollection<SavedRowViewModel> Saved { get; } = [];

    public bool HasSaved => Saved.Count > 0;

    public bool AreSignaturesLocked => _signatures.State == SavedSignaturesState.Unavailable;

    /// <summary>"None saved" is only true when they could be read.</summary>
    public bool ShowsNoneSaved => !HasSaved && !AreSignaturesLocked;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Reads the saved signatures; may touch the Keychain, so call it off the UI thread first.</summary>
    public void LoadSignatures()
    {
        Saved.Clear();
        foreach (SavedSignature saved in _signatures.Of(SignatureKind.Signature).Concat(_signatures.Of(SignatureKind.Initials)))
        {
            Saved.Add(new SavedRowViewModel(saved));
        }

        OnPropertyChanged(nameof(HasSaved));
        OnPropertyChanged(nameof(AreSignaturesLocked));
        OnPropertyChanged(nameof(ShowsNoneSaved));
    }

    /// <summary>
    /// "Start again without them…": for saved signatures whose key never comes back. The old file is set aside, not
    /// deleted, and new ones can be kept from now on.
    /// </summary>
    [RelayCommand]
    private async Task StartSignaturesAgainAsync()
    {
        if (!await _confirm(
                "Start again without your saved signatures?",
                "Fillsign can’t open the ones saved now, so you won’t be able to use them. New signatures and initials you keep will be saved from now on. Documents you’ve already saved or exported keep theirs.",
                "Start again").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _signatures.StartAgain();
            LoadSignatures();
            Message = "You can keep new signatures and initials again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Fillsign couldn’t set the old ones aside. Try again in a moment.";
            System.Diagnostics.Trace.TraceWarning($"Saved signatures could not be set aside. {ex.Message}");
        }
    }

    /// <summary>"Remove…" (D10): asks, then removes it from this computer. Documents keep it.</summary>
    [RelayCommand]
    private async Task RemoveSignatureAsync(SavedRowViewModel row)
    {
        string noun = row.Saved.Kind == SignatureKind.Initials ? "these initials" : "this signature";
        if (!await _confirm(
                $"Remove {noun}?",
                "It will be removed from this computer and can’t be brought back. Documents you’ve already saved or exported keep it.",
                row.Saved.Kind == SignatureKind.Initials ? "Remove initials" : "Remove signature").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _signatures.Remove(row.Saved.Id);
            Saved.Remove(row);
            OnPropertyChanged(nameof(HasSaved));
            OnPropertyChanged(nameof(ShowsNoneSaved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SignatureKeyUnavailableException)
        {
            Message = "Fillsign couldn’t remove it. Try again in a moment.";
            System.Diagnostics.Trace.TraceWarning($"A saved signature could not be removed. {ex.Message}");
        }
    }
}

/// <summary>A saved signature or initials in Settings: "Signature · last used 12/11/2026".</summary>
public sealed class SavedRowViewModel(SavedSignature saved)
{
    public SavedSignature Saved { get; } = saved;

    public Bitmap Thumbnail { get; } = SignatureDialogViewModel.ToBitmap(saved.Image.Png.AsSpan());

    public string Description =>
        $"{(Saved.Kind == SignatureKind.Initials ? "Initials" : "Signature")}{(Saved.Method == SignatureMethod.Drawn ? string.Empty : $" ({Saved.Method.ToString().ToLowerInvariant()})")} · last used {Saved.LastUsed.ToLocalTime():dd/MM/yyyy}";
}
