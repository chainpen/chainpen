using CommunityToolkit.Mvvm.Input;
using Fillsign.Core.Editing;
using Fillsign.Core.Model;

namespace Fillsign.App.ViewModels;

/// <summary>
/// FS-952: bold and alignment for text. The buttons change the text being typed or the selected text; with nothing
/// selected they set how new text (and dates) start, for the rest of the session.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Whether new text starts bold.</summary>
    public bool DefaultBold { get; private set; }

    /// <summary>How new text lines up.</summary>
    public TextAlign DefaultAlign { get; private set; } = TextAlign.Left;

    /// <summary>The bar shows Bold and alignment: typing, text selected, or the Text or Date tool with nothing selected.</summary>
    public bool OptionsHaveTextStyle => TypingItem is not null || (Selection.IsEmpty
        ? Tool is EditorTool.Text or EditorTool.Date
        : SelectedItems().Any(item => item is TextItem));

    public bool IsBoldChosen => StyledText()?.IsBold ?? DefaultBold;

    public bool IsAlignLeft => (StyledText()?.Align ?? DefaultAlign) == TextAlign.Left;

    public bool IsAlignCentre => (StyledText()?.Align ?? DefaultAlign) == TextAlign.Centre;

    public bool IsAlignRight => (StyledText()?.Align ?? DefaultAlign) == TextAlign.Right;

    /// <summary>Bold on or off (Mod+B).</summary>
    [RelayCommand]
    private void ToggleBold() => ApplyTextStyle(isBold: !IsBoldChosen, align: null);

    [RelayCommand]
    private void SetAlign(TextAlign align) => ApplyTextStyle(isBold: null, align);

    private void ApplyTextStyle(bool? isBold, TextAlign? align)
    {
        if (IsViewOnly)
        {
            return;
        }

        if (TypingItem is { } typing)
        {
            ApplyLive(ItemEdits.SetTextStyle(State, new HashSet<ItemId> { typing }, isBold, align));
        }
        else if (SelectedItems().Any(item => item is TextItem))
        {
            Change(ItemEdits.SetTextStyle(State, SelectedIds, isBold, align));
        }

        // The last choice also sets how new text starts, as colour and size do.
        DefaultBold = isBold ?? DefaultBold;
        DefaultAlign = align ?? DefaultAlign;
        Changed();
    }

    /// <summary>The text whose style the buttons show: the one being typed, else the first selected.</summary>
    private TextItem? StyledText() =>
        TypingItem is { } typing ? State.FindItem(typing) as TextItem : SelectedItems().OfType<TextItem>().FirstOrDefault();

    private void OnTextStyleChanged()
    {
        OnPropertyChanged(nameof(OptionsHaveTextStyle));
        OnPropertyChanged(nameof(IsBoldChosen));
        OnPropertyChanged(nameof(IsAlignLeft));
        OnPropertyChanged(nameof(IsAlignCentre));
        OnPropertyChanged(nameof(IsAlignRight));
    }
}
