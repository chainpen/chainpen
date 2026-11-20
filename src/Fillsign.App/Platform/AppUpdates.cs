using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

namespace Fillsign.App.Platform;

/// <summary>What a check for updates found.</summary>
public enum UpdateCheck
{
    /// <summary>Running from source or a portable copy, which can't update itself.</summary>
    NotInstalled,
    UpToDate,

    /// <summary>An update was downloaded (or already had been) and is ready to install.</summary>
    Ready,

    /// <summary>Offline, rate-limited, or the release list couldn't be read.</summary>
    Failed,
}

/// <summary>An update that has been downloaded and is ready to install (🎨 10).</summary>
public sealed record UpdateReady(string Version, IReadOnlyList<string> Notes);

/// <summary>
/// Auto-update from GitHub Releases (FS-804, FR-10.2). Installed copies check quietly in the background, download the
/// update, then show "Update ready" in the status bar; nothing interrupts. Running from source, or a portable copy that
/// Velopack didn't install, never checks. Only the release list on GitHub is fetched; nothing about the person or their
/// documents is sent.
/// </summary>
public sealed partial class AppUpdates : ObservableObject
{
    public const string ReleasesUrl = "https://github.com/fillsign/fillsign";

    private UpdateManager? _manager;
    private VelopackAsset? _downloaded;

    public static AppUpdates Instance { get; } = new();

    /// <summary>
    /// Keeps everyone's unsaved work as recovery copies before a restart, and says whether it all was; set by the app
    /// at start-up.
    /// </summary>
    public Func<Task<bool>>? KeepUnsavedWork { get; set; }

    /// <summary>Why the last "Restart and update" didn't restart, shown under its button; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestartProblem))]
    public partial string RestartProblem { get; private set; } = string.Empty;

    public bool HasRestartProblem => RestartProblem.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(Title))]
    public partial UpdateReady? Ready { get; private set; }

    public bool IsReady => Ready is not null;

    public string Title => Ready is { } ready ? $"Fillsign {ready.Version} is ready to install" : string.Empty;

    /// <summary>Shows an update as ready without downloading one, or none with null. For tests and screenshots.</summary>
    internal void ShowReady(UpdateReady? ready) => Ready = ready;

    /// <summary>
    /// Checks for an update and downloads it. Runs quietly a little after start-up, so opening a document isn't slowed
    /// down, and again when the person chooses Check for Updates, who is then told what was found. A check already
    /// under way is shared rather than started twice.
    /// </summary>
    public Task<UpdateCheck> CheckAsync()
    {
        if (_checking is not { IsCompleted: false })
        {
            _checking = CheckOnceAsync();
        }

        return _checking;
    }

    private Task<UpdateCheck>? _checking;

    private async Task<UpdateCheck> CheckOnceAsync()
    {
        try
        {
            if (Ready is not null)
            {
                return UpdateCheck.Ready;
            }

            if (CreateManager() is not { IsInstalled: true } manager)
            {
                return UpdateCheck.NotInstalled;
            }

            _manager = manager;

            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            if (update is null)
            {
                return UpdateCheck.UpToDate;
            }

            await manager.DownloadUpdatesAsync(update, progress: null, CancellationToken.None).ConfigureAwait(true);
            _downloaded = update.TargetFullRelease;
            Ready = new UpdateReady(_downloaded.Version.ToString(), NotesFrom(_downloaded.NotesMarkdown));
            return UpdateCheck.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            // Offline, rate-limited or no releases yet. The quiet check just tries again next launch.
            System.Diagnostics.Trace.TraceInformation($"No update this time. {ex.Message}");
            return UpdateCheck.Failed;
        }
    }

    /// <summary>The update manager, or null when Velopack can't find an installation to update (running from source).</summary>
    private UpdateManager? CreateManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        try
        {
            return new UpdateManager(CreateSource(Environment.GetEnvironmentVariable(TestFolderVariable)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceInformation($"Not an installed copy, so no updates. {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Names a folder of packed releases to update from instead of GitHub, to try an update from one local build to
    /// the next before anything is published. Only someone who can already start Fillsign with their own environment
    /// can set it.
    /// </summary>
    public const string TestFolderVariable = "FILLSIGN_UPDATE_FOLDER";

    /// <summary>GitHub Releases, or the folder <see cref="TestFolderVariable"/> names when it's set.</summary>
    internal static IUpdateSource CreateSource(string? testFolder) =>
        string.IsNullOrWhiteSpace(testFolder)
            ? new GithubSource(ReleasesUrl, accessToken: null, prerelease: false, downloader: null)
            : new SimpleFileSource(new DirectoryInfo(testFolder));

    /// <summary>What to tell someone who chose Check for Updates.</summary>
    public static (string Title, string Message) Describe(UpdateCheck result, string currentVersion, UpdateReady? ready) => result switch
    {
        UpdateCheck.UpToDate => ("You’re up to date", $"Fillsign {currentVersion} is the latest version."),
        UpdateCheck.Ready => ("Update ready", $"Fillsign {ready?.Version} has been downloaded. Choose Update ready at the bottom of the window to install it; your work is kept."),
        UpdateCheck.NotInstalled => ("Updates aren’t available for this copy", $"This copy of Fillsign ({currentVersion}) wasn’t installed with the Fillsign installer, so it can’t update itself. Download the latest version from the Fillsign releases page."),
        _ => ("Couldn’t check for updates", "Fillsign couldn’t reach the update server. Check your internet connection and try again later."),
    };

    /// <summary>"Restart and update": unsaved work is kept as recovery copies and offered back after the restart.</summary>
    [RelayCommand]
    private async Task RestartAndUpdateAsync()
    {
        if (_manager is null || _downloaded is null)
        {
            return;
        }

        // Never restart over work that couldn't be kept: say so, and leave everything open.
        if (KeepUnsavedWork is { } keep && !await keep().ConfigureAwait(true))
        {
            RestartProblem = "Fillsign couldn’t keep a copy of your unsaved work, so it hasn’t restarted. Wait for every page to load, or save your work, then try again.";
            return;
        }

        RestartProblem = string.Empty;

        _manager.ApplyUpdatesAndRestart(_downloaded);
    }

    /// <summary>"When I close Fillsign": installs quietly after the last window closes.</summary>
    [RelayCommand]
    private void UpdateWhenClosed()
    {
        if (_manager is null || _downloaded is null)
        {
            return;
        }

        _manager.WaitExitThenApplyUpdates(_downloaded, silent: true, restart: false);
        Ready = null;
    }

    /// <summary>The release notes' bullet points ("- Faster opening for large scans"), at most five.</summary>
    internal static IReadOnlyList<string> NotesFrom(string? markdown) =>
        [.. (markdown ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(line => line.Length > 0)
            .Take(5)];
}
