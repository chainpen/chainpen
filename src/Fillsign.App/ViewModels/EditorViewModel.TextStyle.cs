using CommunityToolkit.Mvvm.Input;
using Fillsign.Core.Editing;
using Fillsign.Core.Model;

namespace Fillsign.App.ViewModels;

/// <summary>
/// FS-952: bold text. The button changes the text being typed or the selected text; with nothing selected it sets how
/// new text (and dates) start, for the rest of the session. Alignment isn't offered: text has no box to line up in, so
/// it only moved a multi-line item's lines against each other. Working copies that use it still show it.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Whether new text starts bold.</summary>
    public bool DefaultBold { get; private set; }

    /// <summary>The bar shows Bold: typing, text selected, or the Text or Date tool with nothing selected.</summary>
    public bool OptionsHaveTextStyle => TypingItem is not null || (Selection.IsEmpty
        ? Tool is EditorTool.Text or EditorTool.Date
        : SelectedItems().Any(item => item is TextItem));

    public bool IsBoldChosen => StyledText()?.IsBold ?? DefaultBold;

    /// <summary>Bold on or off (Mod+B).</summary>
    [RelayCommand]
    private void ToggleBold() => ApplyTextStyle(isBold: !IsBoldChosen, align: null);

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
        Changed();
    }

    /// <summary>The text whose style the buttons show: the one being typed, else the first selected.</summary>
    private TextItem? StyledText() =>
        TypingItem is { } typing ? State.FindItem(typing) as TextItem : SelectedItems().OfType<TextItem>().FirstOrDefault();

    private void OnTextStyleChanged()
    {
        OnPropertyChanged(nameof(OptionsHaveTextStyle));
        OnPropertyChanged(nameof(IsBoldChosen));
    }
}
