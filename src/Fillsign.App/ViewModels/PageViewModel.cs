using CommunityToolkit.Mvvm.ComponentModel;
using Fillsign.App.Localization;
using Fillsign.Core.Model;

namespace Fillsign.App.ViewModels;

/// <summary>One page in the page view and the Pages panel.</summary>
public sealed partial class PageViewModel : ObservableObject
{
    public PageViewModel(PageEntry entry, int number, PageSize displaySize, bool isLoaded)
    {
        Entry = entry;
        Number = number;
        DisplaySize = displaySize;
        IsLoaded = isLoaded;
    }

    public PageEntry Entry { get; }

    public PageId Id => Entry.Id;

    /// <summary>1-based position in the document.</summary>
    public int Number { get; }

    /// <summary>Size as displayed, in points, with all rotation applied.</summary>
    public PageSize DisplaySize { get; }

    /// <summary>False while the page's real size is still loading (a skeleton is shown).</summary>
    public bool IsLoaded { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(NumberLabel))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool HasEdits { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(NumberLabel))]
    public partial bool IsDamaged { get; set; }

    /// <summary>Part of the page selection (FR-6.3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsSelected { get; set; }

    public bool IsRotated => Entry.Rotation != Rotation.None;

    /// <summary>"3", "3 · rotated" or "3 · can't show".</summary>
    public string NumberLabel => IsDamaged ? Text.Format(Strings.Editor_PageNumberCantShow, Number)
        : IsRotated ? Text.Format(Strings.Editor_PageNumberRotated, Number)
        : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Page 1, current, has edits".</summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { Text.Format(Strings.Editor_PageNumber, Number) };
            if (IsCurrent)
            {
                parts.Add(Strings.Editor_PageStateCurrent);
            }

            if (IsSelected)
            {
                parts.Add(Strings.Editor_PageStateSelected);
            }

            if (IsRotated)
            {
                parts.Add(Strings.Editor_PageStateRotated);
            }

            if (HasEdits)
            {
                parts.Add(Strings.Editor_PageStateHasEdits);
            }

            if (IsDamaged)
            {
                parts.Add(Strings.Editor_PageStateCantBeShown);
            }

            return string.Join(Strings.Editor_ListSeparator, parts);
        }
    }

    /// <summary>The interface language changed: the page's labels are read again.</summary>
    public void RefreshText() => OnPropertyChanged(string.Empty);
}
