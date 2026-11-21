using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fillsign.App.Platform.Signatures;
using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Signatures;

namespace Fillsign.App.ViewModels;

/// <summary>How a new signature is made (D1 Draw, D2 Type, D3 Photo or scan).</summary>
public enum SignatureTab
{
    Draw,
    Type,
    Photo,
}

/// <summary>What the dialog is showing: the form, or the "Remove this signature?" confirmation (D10).</summary>
public enum SignatureDialogPage
{
    Form,
    ConfirmRemove,
}

/// <summary>
/// The signature dialog (FR-3.4, FR-4, INTERACTION-PATTERNS §8): one choose-then-place flow. Pick a saved signature or
/// <b>New</b>, then <b>Place signature</b>. The Signature/Initials switch changes the title, buttons and saved list.
/// </summary>
public sealed partial class SignatureDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>Photos bigger than this are almost certainly not a signature, and would be slow to decode.</summary>
    public const long MaxPhotoBytes = 50L * 1024 * 1024;

    private readonly SavedSignatureStore _store;
    private readonly Func<Task<string?>> _choosePhotoFile;
    private readonly TimeProvider _clock;
    private readonly TaskCompletionSource<(SignatureImage Image, SignatureKind Kind)?> _result = new();
    private SignaturePhoto? _photo;

    /// <param name="store">Saved signatures, already loaded (loading can touch the Keychain, so it happens off the UI thread).</param>
    /// <param name="choosePhotoFile">Shows the file picker for a photo; null when cancelled.</param>
    public SignatureDialogViewModel(SavedSignatureStore store, Func<Task<string?>> choosePhotoFile, TimeProvider clock)
    {
        _store = store;
        _choosePhotoFile = choosePhotoFile;
        _clock = clock;
        Strokes.CollectionChanged += (_, _) => OnDrawingChanged();
        SelectedStyle = StyleOptions[0];
        RefreshSaved();
    }

    /// <summary>The image and kind to place, or null when the dialog was cancelled.</summary>
    public Task<(SignatureImage Image, SignatureKind Kind)?> Result => _result.Task;

    // ----- Signature or initials -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignature), nameof(IsInitials), nameof(Title), nameof(PlaceLabel), nameof(Noun), nameof(ChooseHeading),
        nameof(NewOptionLabel), nameof(PadHint), nameof(KeepLabel), nameof(TypePlaceholder), nameof(TypeLabel), nameof(KeepDetail))]
    public partial SignatureKind Kind { get; set; } = SignatureKind.Signature;

    public bool IsSignature
    {
        get => Kind == SignatureKind.Signature;
        set
        {
            if (value)
            {
                Kind = SignatureKind.Signature;
            }
        }
    }

    public bool IsInitials
    {
        get => Kind == SignatureKind.Initials;
        set
        {
            if (value)
            {
                Kind = SignatureKind.Initials;
            }
        }
    }

    /// <summary>"signature" or "initials", for sentences.</summary>
    public string Noun => IsInitials ? "initials" : "signature";

    public string Title => IsInitials ? "Add your initials" : HasSaved ? "Add a signature" : "Add your signature";

    public string PlaceLabel => IsInitials ? "Place initials" : "Place signature";

    public string ChooseHeading => IsInitials ? "Choose your initials" : "Choose a signature";

    public string NewOptionLabel => IsInitials ? "New" : "New signature";

    // ----- Saved signatures (FR-4.5, 4.6) -----

    /// <summary>The saved ones of the current kind, most recently used first, then <b>New</b>.</summary>
    public ObservableCollection<SignatureOptionViewModel> Options { get; } = [];

    /// <summary>The chosen option: a saved one, or <b>New</b>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSaved), nameof(IsCreating), nameof(IsSavedSelected), nameof(SavedHint), nameof(RemoveLabel), nameof(SavedError))]
    public partial SignatureOptionViewModel? SelectedOption { get; set; }

    /// <summary>The saved one to place, or null for a new one.</summary>
    public SignatureOptionViewModel? SelectedSaved => SelectedOption is { IsNew: false } option ? option : null;

    public bool HasSaved => Options.Any(option => !option.IsNew);

    /// <summary>Whether the Draw / Type / Photo part shows: no saved one chosen.</summary>
    public bool IsCreating => SelectedSaved is null;

    public bool IsSavedSelected => SelectedSaved is not null;

    /// <summary>"Last used 12/11/2026. Choose New signature to draw, type or use a photo."</summary>
    public string SavedHint => SelectedSaved is { } saved
        ? $"Last used {saved.LastUsedText}. Choose {NewOptionLabel} to draw, type or use a photo."
        : string.Empty;

    /// <summary>Shown when there's no saved one yet (D1).</summary>
    public string FirstTimeHint => $"You haven’t added {(IsInitials ? "your initials" : "a signature")} yet. Draw, type, or use a photo.";

    // ----- Making a new one -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrawTab), nameof(IsTypeTab), nameof(IsPhotoTab), nameof(ShowsInk))]
    public partial SignatureTab Tab { get; set; } = SignatureTab.Draw;

    public bool IsDrawTab
    {
        get => Tab == SignatureTab.Draw;
        set
        {
            if (value)
            {
                Tab = SignatureTab.Draw;
            }
        }
    }

    public bool IsTypeTab
    {
        get => Tab == SignatureTab.Type;
        set
        {
            if (value)
            {
                Tab = SignatureTab.Type;
            }
        }
    }

    public bool IsPhotoTab
    {
        get => Tab == SignatureTab.Photo;
        set
        {
            if (value)
            {
                Tab = SignatureTab.Photo;
            }
        }
    }

    /// <summary>Photos keep their own colours, so the ink choice is only for drawing and typing.</summary>
    public bool ShowsInk => Tab != SignatureTab.Photo;

    /// <summary>Black or blue (FR-4.1). Black is the default: it's what most people sign with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlackInk), nameof(IsBlueInk), nameof(InkBrush))]
    public partial InkColor Ink { get; set; } = InkColor.Black;

    /// <summary>The ink as a brush, for the typed-style previews.</summary>
    public IBrush InkBrush => new SolidColorBrush(Color.FromRgb(Ink.R, Ink.G, Ink.B));

    public bool IsBlackInk
    {
        get => Ink == InkColor.Black;
        set
        {
            if (value)
            {
                Ink = InkColor.Black;
            }
        }
    }

    public bool IsBlueInk
    {
        get => Ink == InkColor.Blue;
        set
        {
            if (value)
            {
                Ink = InkColor.Blue;
            }
        }
    }

    // Draw

    /// <summary>Finished strokes, in pad units. The pad adds to this; Clear empties it.</summary>
    public ObservableCollection<IReadOnlyList<InkSample>> Strokes { get; } = [];

    public bool HasInk => Strokes.Count > 0;

    public string PadHint => IsInitials
        ? "Write your initials here with your mouse, trackpad, pen or finger"
        : "Sign here with your mouse, trackpad, pen or finger";

    // Type

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypedPreview))]
    public partial string TypedName { get; set; } = string.Empty;

    /// <summary>The three handwriting styles, each showing the typed name in its font (D2).</summary>
    public IReadOnlyList<StyleOptionViewModel> StyleOptions { get; } = [.. HandwritingFonts.All.Select(style => new StyleOptionViewModel(style))];

    [ObservableProperty]
    public partial StyleOptionViewModel? SelectedStyle { get; set; }

    public HandwritingStyle Style => SelectedStyle?.Style ?? HandwritingStyle.Caveat;

    public string TypeLabel => IsInitials ? "Your initials" : "Your name";

    public string TypePlaceholder => IsInitials ? "For example JL" : "For example Jordan Lee";

    /// <summary>What the style buttons show: the typed name, or a sample until something is typed.</summary>
    public string TypedPreview => TypedName.Trim().Length > 0 ? TypedName.Trim() : IsInitials ? "JL" : "Your name";

    // Photo (FR-4.3)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    public partial string? PhotoName { get; private set; }

    [ObservableProperty]
    public partial string? PhotoSize { get; private set; }

    public bool HasPhoto => PhotoName is not null;

    /// <summary>Removing the paper is preselected: it's what almost everyone wants (INTERACTION-PATTERNS §12).</summary>
    [ObservableProperty]
    public partial bool RemoveBackground { get; set; } = true;

    [ObservableProperty]
    public partial Bitmap? PhotoOriginal { get; private set; }

    [ObservableProperty]
    public partial Bitmap? PhotoResult { get; private set; }

    // Keeping it (FR-4.5: opt-in, never preselected)

    [ObservableProperty]
    public partial bool KeepOnComputer { get; set; }

    public bool CanKeep => _store.State == SavedSignaturesState.Ready;

    public string KeepLabel => $"Keep {(IsInitials ? "these initials" : "this signature")} on this computer for next time";

    /// <summary>Why keeping isn't possible, or what keeping will replace.</summary>
    public string? KeepDetail
    {
        get
        {
            if (!CanKeep)
            {
                return $"Saved {Noun} can’t be opened right now, so this one can’t be kept.";
            }

            string? replaces = _store.Of(Kind).Count >= SavedSignatureStore.MaxPerKind
                ? $"You have {SavedSignatureStore.MaxPerKind} saved. Keeping this one replaces the one used longest ago."
                : null;
            string? unencrypted = _store.IsEncrypted
                ? null
                : "This computer has no keyring, so it’s kept unencrypted, readable only by you.";
            return replaces is null || unencrypted is null ? replaces ?? unencrypted : $"{replaces} {unencrypted}";
        }
    }

    // ----- Messages and removal -----

    /// <summary>What's needed before placing (FR-4.8). Clears as soon as it's fixed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(SavedError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>A problem with the chosen saved one (it couldn't be removed), shown above the buttons.</summary>
    public string? SavedError => IsSavedSelected ? Error : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsForm), nameof(IsConfirmingRemove))]
    public partial SignatureDialogPage Page { get; private set; } = SignatureDialogPage.Form;

    public bool IsForm => Page == SignatureDialogPage.Form;

    public bool IsConfirmingRemove => Page == SignatureDialogPage.ConfirmRemove;

    public string RemoveTitle => $"Remove {(SelectedSaved?.Kind == SignatureKind.Initials ? "these initials" : "this signature")}?";

    public string RemoveButtonLabel => SelectedSaved?.Kind == SignatureKind.Initials ? "Remove initials" : "Remove signature";

    public string RemoveLabel => SelectedSaved?.Kind == SignatureKind.Initials ? "Remove these initials…" : "Remove this signature…";

    // ----- Commands -----

    [RelayCommand]
    private void ClearPad() => Strokes.Clear();

    [RelayCommand]
    private async Task ChoosePhotoAsync()
    {
        string? path = await _choosePhotoFile().ConfigureAwait(true);
        if (path is not null)
        {
            await LoadPhotoAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Reads and previews a photo. Also used for a photo dropped on the dialog.</summary>
    public async Task LoadPhotoAsync(string path)
    {
        Error = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPhotoBytes)
            {
                Error = "That file is too big to be a photo of a signature. Choose a photo under 50 MB.";
                return;
            }

            SignaturePhoto loaded = await Task.Run(() => SignaturePhoto.Load(File.ReadAllBytes(path))).ConfigureAwait(true);
            _photo?.Dispose();
            _photo = loaded;
            PhotoName = info.Name;
            PhotoSize = FormatSize(info.Length);
            PhotoOriginal = ToBitmap(loaded.OriginalPng.AsSpan());
            UpdatePhotoResult();
        }
        catch (InvalidDataException)
        {
            Error = "That file couldn’t be read as a picture. Choose a JPG or PNG photo or scan.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Fillsign couldn’t open {Path.GetFileName(path)}. Check it’s still there and try again.";
        }
    }

    [RelayCommand]
    private void Place()
    {
        if (SelectedSaved is { } saved)
        {
            try
            {
                _store.MarkUsed(saved.Id, _clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SignatureKeyUnavailableException)
            {
                // Only the "last used" order is lost; placing it still works.
                System.Diagnostics.Trace.TraceWarning($"A saved signature's last-used time could not be recorded. {ex.Message}");
            }

            _result.TrySetResult((saved.Image, Kind));
            return;
        }

        (SignatureImage? image, SignatureMethod method) = Tab switch
        {
            SignatureTab.Draw => (SignatureImageMaker.FromInk(Strokes, Ink), SignatureMethod.Drawn),
            SignatureTab.Type => (SignatureImageMaker.FromText(TypedName, Style, Ink), SignatureMethod.Typed),
            _ => (_photo?.MakeSignature(RemoveBackground), SignatureMethod.Photo),
        };

        if (image is null)
        {
            Error = MissingMessage();
            return;
        }

        if (KeepOnComputer && CanKeep)
        {
            try
            {
                _store.Keep(new SavedSignature(image, Kind, method, _clock.GetUtcNow()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SignatureKeyUnavailableException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceWarning($"A signature could not be kept. {ex.Message}");
                Error = $"Fillsign couldn’t keep this {Noun} on this computer. Untick “Keep…” to place it without keeping it.";
                return;
            }
        }

        _result.TrySetResult((image, Kind));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsConfirmingRemove)
        {
            Page = SignatureDialogPage.Form;
            return;
        }

        _result.TrySetResult(null);
    }

    [RelayCommand]
    private void AskToRemove()
    {
        if (SelectedSaved is null)
        {
            return;
        }

        OnPropertyChanged(nameof(RemoveTitle));
        OnPropertyChanged(nameof(RemoveButtonLabel));
        Page = SignatureDialogPage.ConfirmRemove;
    }

    /// <summary>Removes the chosen saved signature from this computer (FR-4.7, D10).</summary>
    [RelayCommand]
    private void ConfirmRemove()
    {
        string? problem = null;
        if (SelectedSaved is { } saved)
        {
            try
            {
                _store.Remove(saved.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SignatureKeyUnavailableException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceWarning($"A saved signature could not be removed. {ex.Message}");
                problem = "Fillsign couldn’t remove it. Try again in a moment.";
            }
        }

        Page = SignatureDialogPage.Form;
        RefreshSaved();

        // After refreshing: choosing an option clears the error, and refreshing chooses one.
        Error = problem;
    }

    /// <summary>
    /// Frees the decoded photo. Preview bitmaps are left to the garbage collector: the renderer may still be drawing
    /// the last frame that shows them.
    /// </summary>
    public void Dispose() => _photo?.Dispose();

    // ----- Change handlers -----

    partial void OnKindChanged(SignatureKind value)
    {
        Error = null;
        RefreshSaved();
        OnPropertyChanged(nameof(FirstTimeHint));
        OnPropertyChanged(nameof(TypedPreview));
    }

    partial void OnTabChanged(SignatureTab value) => Error = null;

    partial void OnSelectedOptionChanged(SignatureOptionViewModel? value) => Error = null;

    partial void OnTypedNameChanged(string value)
    {
        if (value.Trim().Length > 0)
        {
            Error = null;
        }
    }

    partial void OnRemoveBackgroundChanged(bool value) => UpdatePhotoResult();

    private void OnDrawingChanged()
    {
        OnPropertyChanged(nameof(HasInk));
        if (Strokes.Count > 0)
        {
            Error = null;
        }
    }

    /// <summary>Shows the saved ones of the current kind, with the most recently used selected (FR-4.6).</summary>
    private void RefreshSaved()
    {
        Options.Clear();
        foreach (SavedSignature saved in _store.Of(Kind))
        {
            Options.Add(new SignatureOptionViewModel(saved, NewOptionLabel));
        }

        if (Options.Count > 0)
        {
            Options.Add(new SignatureOptionViewModel(null, NewOptionLabel));
        }

        // The most recently used is ready to place in one step (FR-4.6); with none saved, the form shows straight away.
        SelectedOption = Options.FirstOrDefault();
        OnPropertyChanged(nameof(HasSaved));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(KeepDetail));
        OnPropertyChanged(nameof(CanKeep));
    }

    private void UpdatePhotoResult()
    {
        PhotoResult = _photo?.MakeSignature(RemoveBackground) is { } image ? ToBitmap(image.Png.AsSpan()) : null;
        if (_photo is not null && PhotoResult is null)
        {
            Error = "Fillsign couldn’t find a signature in this photo. Untick “Remove the paper background”, or choose a clearer photo.";
        }
        else if (PhotoResult is not null)
        {
            Error = null;
        }
    }

    private string MissingMessage() => Tab switch
    {
        SignatureTab.Draw => IsInitials ? "Write your initials in the box first, or use the Type tab." : "Sign in the box first, or use the Type tab.",
        SignatureTab.Type => IsInitials ? "Type your initials first." : "Type your name first.",
        _ when _photo is null => "Choose a photo or scan of your signature first.",
        _ => "Fillsign couldn’t find a signature in this photo. Untick “Remove the paper background”, or choose a clearer photo.",
    };

    internal static Bitmap ToBitmap(ReadOnlySpan<byte> png)
    {
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };
}

/// <summary>One choice in the "Choose a signature" list (D1): a saved signature, or <b>New</b>.</summary>
public sealed class SignatureOptionViewModel
{
    private readonly SavedSignature? _saved;

    public SignatureOptionViewModel(SavedSignature? saved, string newLabel)
    {
        _saved = saved;
        NewLabel = newLabel;
        Thumbnail = saved is null ? null : SignatureDialogViewModel.ToBitmap(saved.Image.Png.AsSpan());
    }

    public bool IsNew => _saved is null;

    public SavedSignature Saved => _saved ?? throw new InvalidOperationException("The New option has no saved signature.");

    public Guid Id => Saved.Id;

    public SignatureImage Image => Saved.Image;

    public SignatureKind Kind => Saved.Kind;

    public Bitmap? Thumbnail { get; }

    /// <summary>"New signature" or "New".</summary>
    public string NewLabel { get; }

    public string LastUsedText => Platform.DateFormats.Format(Saved.LastUsed.ToLocalTime().DateTime);

    /// <summary>"Saved signature, drawn, last used 12/11/2026" for screen readers.</summary>
    public string AccessibleName => IsNew
        ? $"{NewLabel}: draw, type or use a photo"
        : $"Saved {(Kind == SignatureKind.Initials ? "initials" : "signature")}, {Saved.Method.ToString().ToLowerInvariant()}, last used {LastUsedText}";

    /// <summary>"Drawn · last used 12/11/2026", shown when confirming removal.</summary>
    public string Description => $"{Saved.Method} · last used {LastUsedText}";
}

/// <summary>One handwriting style button on the Type tab (D2).</summary>
public sealed class StyleOptionViewModel(HandwritingStyle style)
{
    public HandwritingStyle Style { get; } = style;

    public FontFamily FontFamily { get; } = new($"avares://Fillsign.App/Assets/Fonts/Handwriting#{HandwritingFonts.FamilyName(style)}");

    public FontWeight FontWeight { get; } = (FontWeight)HandwritingFonts.Weight(style);

    /// <summary>Caveat is drawn small for its size, Kalam large, so each gets a size that looks alike.</summary>
    public double PreviewSize { get; } = style switch
    {
        HandwritingStyle.Caveat => 32,
        HandwritingStyle.DancingScript => 26,
        _ => 24,
    };

    public string AccessibleName { get; } = $"{HandwritingFonts.FamilyName(style)} style";
}
