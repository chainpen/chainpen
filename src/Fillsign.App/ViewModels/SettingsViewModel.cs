using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Localization;
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
        _dateFormat = DateFormats.Current;
        LanguageOptions =
        [
            new LanguageOption(null, Text.Format(Strings.Settings_LanguageSystem, Languages.FromSystem().Name)),
            .. Languages.Supported.Select(language => new LanguageOption(language.Code, language.Name)),
        ];
        _language = LanguageOptions.FirstOrDefault(option => option.Code == settings.Language) ?? LanguageOptions[0];
    }

    // ----- Language -----

    /// <summary>"Match system (English)", then each language in its own name.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    private LanguageOption _language;

    public LanguageOption SelectedLanguage
    {
        get => _language;
        set
        {
            // The list reports no selection (null) while it's being rebuilt.
            if (value is null || value == _language)
            {
                return;
            }

            _language = value;
            AppSettings.Update(settings => settings with { Language = value.Code });
            OnPropertyChanged();

            // After this change has finished, so the list closes before the windows are rebuilt in the new language.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Languages.Apply(value.Code));
        }
    }

    // ----- Date format -----

    private DateFormatChoice _dateFormat;

    public bool IsDayMonthYear
    {
        get => _dateFormat == DateFormatChoice.DayMonthYear;
        set => SetDateFormat(value, DateFormatChoice.DayMonthYear);
    }

    public bool IsMonthDayYear
    {
        get => _dateFormat == DateFormatChoice.MonthDayYear;
        set => SetDateFormat(value, DateFormatChoice.MonthDayYear);
    }

    public bool IsYearMonthDay
    {
        get => _dateFormat == DateFormatChoice.YearMonthDay;
        set => SetDateFormat(value, DateFormatChoice.YearMonthDay);
    }

    public bool IsYearMonthDaySlashes
    {
        get => _dateFormat == DateFormatChoice.YearMonthDaySlashes;
        set => SetDateFormat(value, DateFormatChoice.YearMonthDaySlashes);
    }

    public bool IsDayMonthYearDots
    {
        get => _dateFormat == DateFormatChoice.DayMonthYearDots;
        set => SetDateFormat(value, DateFormatChoice.DayMonthYearDots);
    }

    /// <summary>"Today is 21/11/2026 in this format."</summary>
    public string DateExample => Text.Format(Strings.Settings_TodayIsInThisFormat, DateFormats.Format(DateTime.Today, _dateFormat));

    private void SetDateFormat(bool isChosen, DateFormatChoice format)
    {
        if (!isChosen || _dateFormat == format)
        {
            return;
        }

        _dateFormat = format;
        AppSettings.Update(settings => settings with { DateFormat = format });
        OnPropertyChanged(nameof(IsDayMonthYear));
        OnPropertyChanged(nameof(IsMonthDayYear));
        OnPropertyChanged(nameof(IsYearMonthDay));
        OnPropertyChanged(nameof(IsYearMonthDaySlashes));
        OnPropertyChanged(nameof(IsDayMonthYearDots));
        OnPropertyChanged(nameof(DateExample));
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
            Message = Strings.Settings_NoRecoveryCopiesToDelete;
            return;
        }

        if (await _confirm(
                Strings.Settings_DeleteAllRecoveryCopiesTitle,
                Text.Plural(count, Strings.Settings_DeleteRecoveryCopiesBody_One, Strings.Settings_DeleteRecoveryCopiesBody_Other),
                Strings.Settings_DeleteRecoveryCopies).ConfigureAwait(true))
        {
            RecoveryStore.DeleteAll();
            Message = Strings.Settings_RecoveryCopiesDeleted;
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
                Strings.Settings_StartAgainTitle,
                Strings.Settings_StartAgainBody,
                Strings.Settings_StartAgain).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _signatures.StartAgain();
            LoadSignatures();
            Message = Strings.Settings_CanKeepAgain;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Strings.Settings_CouldntSetAside;
            System.Diagnostics.Trace.TraceWarning($"Saved signatures could not be set aside. {ex.Message}");
        }
    }

    /// <summary>"Remove…" (D10): asks, then removes it from this computer. Documents keep it.</summary>
    [RelayCommand]
    private async Task RemoveSignatureAsync(SavedRowViewModel row)
    {
        bool isInitials = row.Saved.Kind == SignatureKind.Initials;
        if (!await _confirm(
                isInitials ? Strings.Signature_RemoveTheseInitialsTitle : Strings.Signature_RemoveThisSignatureTitle,
                Strings.Signature_ItWillBeRemovedFromThis,
                isInitials ? Strings.Signature_RemoveInitials : Strings.Signature_RemoveSignature).ConfigureAwait(true))
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
            Message = Strings.Signature_CouldntRemove;
            System.Diagnostics.Trace.TraceWarning($"A saved signature could not be removed. {ex.Message}");
        }
    }
}

/// <summary>A saved signature or initials in Settings: "Signature · last used 12/11/2026".</summary>
public sealed class SavedRowViewModel(SavedSignature saved)
{
    public SavedSignature Saved { get; } = saved;

    public Bitmap Thumbnail { get; } = SignatureDialogViewModel.ToBitmap(saved.Image.Png.AsSpan());

    public string Description => Text.Format(
        (Saved.Kind, Saved.Method) switch
        {
            (SignatureKind.Initials, SignatureMethod.Typed) => Strings.Settings_SavedInitialsTyped,
            (SignatureKind.Initials, SignatureMethod.Photo) => Strings.Settings_SavedInitialsPhoto,
            (SignatureKind.Initials, _) => Strings.Settings_SavedInitialsDrawn,
            (_, SignatureMethod.Typed) => Strings.Settings_SavedSignatureTyped,
            (_, SignatureMethod.Photo) => Strings.Settings_SavedSignaturePhoto,
            _ => Strings.Settings_SavedSignatureDrawn,
        },
        DateFormats.Format(Saved.LastUsed.ToLocalTime().DateTime));
}

/// <summary>A row in Settings → Language: a language code, or null to follow the computer.</summary>
public sealed record LanguageOption(string? Code, string Name);
