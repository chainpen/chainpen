using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fillsign.App.Platform;
using Fillsign.Pdf.Import;

namespace Fillsign.App.ViewModels;

/// <summary>
/// The start screen (🎨 2, 9): open or drop files, the Recent list (FR-1.7), work to recover after an unexpected
/// shutdown (FR-8.12), and the practice form on the very first launch (FR-1.8).
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    public StartViewModel(bool isFirstLaunch)
    {
        IsFirstLaunch = isFirstLaunch;
        Refresh();
    }

    /// <summary>First launch shows "How it works" and the practice form (FR-1.8).</summary>
    public bool IsFirstLaunch { get; }

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

    public ObservableCollection<RecentItemViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    /// <summary>Unsaved work found at start-up that hasn't been recovered or put off (🎨 2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecovery), nameof(RecoveryText), nameof(RecoveryTitle))]
    public partial RecoveryCopy? Recovery { get; private set; }

    public string RecoveryTitle => Recovery is { IsForUpdate: true } ? "Fillsign was updated." : "Fillsign closed unexpectedly.";

    public bool HasRecovery => Recovery is not null;

    public string RecoveryText => Recovery is { } copy
        ? $"Changes to {copy.Name} from {When(copy.SavedAt)} weren’t saved to the file, and can be recovered. If you choose Not now, you can still recover them from Recent for {AppSettings.Load().RecoveryDays} days."
        : string.Empty;

    // ----- Crash reports (FS-902) -----

    /// <summary>From the second start, until answered: may Fillsign keep reports of unexpected errors to send?</summary>
    [ObservableProperty]
    public partial bool AsksAboutCrashReports { get; private set; }

    /// <summary>A report from an unexpected error is waiting to be sent or deleted.</summary>
    [ObservableProperty]
    public partial bool HasCrashReport { get; private set; }

    public void AnswerCrashReports(bool isYes)
    {
        AppSettings.Update(settings => settings with { SendCrashReports = isYes });
        Refresh();
    }

    /// <summary>The report as a pre-filled GitHub issue to review and send; the waiting reports are then cleared.</summary>
    public Uri? TakeCrashReportLink()
    {
        Uri? link = CrashReports.IssueLinkForNewest();
        CrashReports.DeleteAll();
        Refresh();
        return link;
    }

    public void DeleteCrashReports()
    {
        CrashReports.DeleteAll();
        Refresh();
    }

    /// <summary>Re-reads Recent and the recovery copies, after a window opens or closes or Settings change.</summary>
    public void Refresh()
    {
        AppSettings current = AppSettings.Load();
        AsksAboutCrashReports = !IsFirstLaunch && current.LaunchCount >= 2 && current.SendCrashReports is null;
        HasCrashReport = current.SendCrashReports == true && CrashReports.Pending().Count > 0;

        IReadOnlyList<RecoveryCopy> copies = RecoveryStore.List();
        Recovery = copies.FirstOrDefault(copy => !copy.IsDismissed);

        Recent.Clear();

        // Work that can only be recovered (never saved, or not matching a recent file) comes first.
        var shown = new HashSet<Guid>();
        foreach (RecentFile file in AppSettings.Load().Recent)
        {
            RecoveryCopy? copy = copies.FirstOrDefault(copy => Matches(copy, file.Path));
            if (copy is not null)
            {
                shown.Add(copy.Id);
            }

            Recent.Add(RecentItemViewModel.ForFile(file, copy));
        }

        int insertAt = 0;
        foreach (RecoveryCopy copy in copies.Where(copy => !shown.Contains(copy.Id)))
        {
            Recent.Insert(insertAt++, RecentItemViewModel.ForUnsavedWork(copy));
        }

        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>"Not now": keep it recoverable from Recent without asking again.</summary>
    public void PutOffRecovery()
    {
        if (Recovery is { } copy)
        {
            RecoveryStore.Dismiss(copy.Id);
            Refresh();
        }
    }

    public void ClearRecent()
    {
        AppSettings.Update(settings => settings with { RecentFiles = [] });
        Refresh();
    }

    private static bool Matches(RecoveryCopy copy, string path) =>
        (copy.WorkingCopyPath is { } saved && AppSettings.SamePath(saved, path)) || (copy.OpenedFrom is { } opened && AppSettings.SamePath(opened, path));

    internal static string When(DateTimeOffset when)
    {
        DateTime local = when.ToLocalTime().DateTime;
        return local.Date == DateTime.Today
            ? "today, " + local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : DateFormats.Format(local);
    }
}

/// <summary>One row in Recent: a file, or unsaved work to recover (🎨 2).</summary>
public sealed class RecentItemViewModel
{
    private RecentItemViewModel(string name, string folder, string lastOpened, string status, bool isWarning, string? path, RecoveryCopy? recovery)
    {
        Name = name;
        Folder = folder;
        LastOpened = lastOpened;
        Status = status;
        IsWarning = isWarning;
        Path = path;
        Recovery = recovery;
    }

    public string Name { get; }

    public string Folder { get; }

    public string LastOpened { get; }

    public string Status { get; }

    /// <summary>Shown in the warning colour: there's unsaved work, or the file can't be found.</summary>
    public bool IsWarning { get; }

    public string? Path { get; }

    /// <summary>Opening this row recovers this copy instead of opening the file.</summary>
    public RecoveryCopy? Recovery { get; }

    /// <summary>Most files have nothing to say, so the status only shows when there's one (UX review 9).</summary>
    public bool HasStatus => Status.Length > 0;

    public string AccessibleName => HasStatus ? $"{Name}, {Folder}, last opened {LastOpened}, {Status}" : $"{Name}, {Folder}, last opened {LastOpened}";

    public static RecentItemViewModel ForFile(RecentFile file, RecoveryCopy? recovery)
    {
        string name = System.IO.Path.GetFileName(file.Path);
        string folder = SaveSizes.ShortFolder(System.IO.Path.GetDirectoryName(file.Path) ?? string.Empty);
        string opened = Capitalise(StartViewModel.When(file.LastOpened));
        if (recovery is not null)
        {
            return new(name, folder, opened, "Changes not saved · open to recover", isWarning: true, file.Path, recovery);
        }

        if (!File.Exists(file.Path))
        {
            return new(name, folder, opened, "Can’t find this file", isWarning: true, file.Path, null);
        }

        return new(name, folder, opened, file.IsFinalCopy ? "Final copy (can’t be edited)" : string.Empty, isWarning: false, file.Path, null);
    }

    public static RecentItemViewModel ForUnsavedWork(RecoveryCopy copy) =>
        new(copy.Name, "Not saved to a file", Capitalise(StartViewModel.When(copy.SavedAt)), "Changes not saved · open to recover", isWarning: true, null, copy);

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
