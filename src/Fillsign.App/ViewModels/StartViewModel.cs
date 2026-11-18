using CommunityToolkit.Mvvm.ComponentModel;
using Fillsign.Pdf.Import;

namespace Fillsign.App.ViewModels;

/// <summary>The start screen (🎨 2, 9): open or drop files, or try the practice form on the very first launch.</summary>
public sealed partial class StartViewModel(bool isFirstLaunch) : ObservableObject
{
    /// <summary>First launch shows "How it works" and the practice form (FR-1.8).</summary>
    public bool IsFirstLaunch { get; } = isFirstLaunch;

    public string DropHint => IsFirstLaunch
        ? "PDF, JPG, PNG and other images. Drop several to combine them."
        : "PDF, JPG, PNG and other images. Drop several files to combine them into one document.";

    /// <summary>Why the last open failed (D6), shown above the drop zone until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial OpenProblem? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsOpening { get; set; }
}
