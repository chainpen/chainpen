using CommunityToolkit.Mvvm.ComponentModel;
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
    public string NumberLabel => IsDamaged ? $"{Number} · can’t show"
        : IsRotated ? $"{Number} · rotated"
        : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Page 1, current, has edits".</summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { $"Page {Number}" };
            if (IsCurrent)
            {
                parts.Add("current");
            }

            if (IsSelected)
            {
                parts.Add("selected");
            }

            if (IsRotated)
            {
                parts.Add("rotated");
            }

            if (HasEdits)
            {
                parts.Add("has edits");
            }

            if (IsDamaged)
            {
                parts.Add("can’t be shown");
            }

            return string.Join(", ", parts);
        }
    }
}
