using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fillsign.App.Platform;

/// <summary>How the app looks (FR-10.1). The document itself is never recoloured.</summary>
public enum AppearanceChoice
{
    MatchSystem,
    Light,
    Dark,
}

/// <summary>Where the window was and how big, when it was last closed (screen pixels for the position).</summary>
public sealed record WindowPlacement(double Width, double Height, int X, int Y, bool IsMaximized);

/// <summary>A document in the start screen's Recent list (FR-1.7).</summary>
/// <param name="IsFinalCopy">It was exported as a final copy, so opening it shows the finished pages.</param>
public sealed record RecentFile(string Path, DateTimeOffset LastOpened, bool IsFinalCopy = false);

/// <summary>
/// App preferences kept in the OS app data folder (FR-10.1): the launch count (first-launch screen, FR-1.8), the
/// appearance, how long recovery copies are kept, the crash-report choice and the
/// Recent list, the last ink colour chosen (#rrggbb), the date format (null until the first start stores the system's), the interface language (null follows the system), and whether single letters pick tools (FS-951; off unless turned on, as most people filling a form won't know the letters). Unreadable settings fall back to defaults. Changes take effect immediately and are saved straight away.
/// </summary>
public sealed record AppSettings(
    int LaunchCount = 0,
    AppearanceChoice Appearance = AppearanceChoice.MatchSystem,
    int RecoveryDays = AppSettings.DefaultRecoveryDays,
    bool? SendCrashReports = null,
    ImmutableArray<RecentFile> RecentFiles = default,
    string? InkColour = null,
    bool UseSingleKeyTools = false,
    double PagesPanelWidth = AppSettings.DefaultPagesPanelWidth,
    WindowPlacement? Window = null,
    DateFormatChoice? DateFormat = null,
    string? Language = null)
{
    public const int DefaultRecoveryDays = 30;

    /// <summary>The pages panel's width in the editor, and how far it can be dragged either way.</summary>
    public const double DefaultPagesPanelWidth = 196;
    public const double MinPagesPanelWidth = 150;
    public const double MaxPagesPanelWidth = 420;
    public const int MaxRecentFiles = 10;

    /// <summary>The choices Settings offers for how long recovery copies are kept.</summary>
    public static readonly ImmutableArray<int> RecoveryDayChoices = [1, 7, 30];

    /// <summary>Tests point this at a temporary folder.</summary>
    internal static string? FolderOverride { get; set; }

    public static string Folder
    {
        get
        {
            if (FolderOverride is { } overridden)
            {
                return overridden;
            }

            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Fillsign");
            }

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fillsign");
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string config = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
            return Path.Combine(config, "Fillsign");
        }
    }

    public ImmutableArray<RecentFile> Recent => RecentFiles.IsDefault ? [] : RecentFiles;

    /// <summary>The ink colour new items start with: the last one chosen, or black.</summary>
    public Core.Model.InkColor Ink => Core.Model.InkColor.TryParseHex(InkColour, out Core.Model.InkColor colour) ? colour : Core.Model.InkColor.Default;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            AppSettings settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
            return settings.Validated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning($"Settings could not be read; using defaults. {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Saves these settings. Failing to save isn't worth interrupting anyone over.</summary>
    public void Save()
    {
        try
        {
            // Recent holds document names and folders, so only this user can read it.
            PrivateFile.WriteReplacing(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Settings could not be saved. {ex.Message}");
        }
    }

    /// <summary>Loads, changes and saves in one step, so two windows don't overwrite each other's changes.</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (s_lock)
        {
            AppSettings changed = change(Load()).Validated();
            changed.Save();
            Changed?.Invoke(changed);
            return changed;
        }
    }

    /// <summary>Raised after <see cref="Update"/>, so open windows and the theme follow straight away.</summary>
    public static event Action<AppSettings>? Changed;

    /// <summary>Puts a file at the top of Recent (FR-1.7), keeping the list short.</summary>
    public AppSettings WithRecent(string path, DateTimeOffset when, bool isFinalCopy = false)
    {
        string full = System.IO.Path.GetFullPath(path);
        ImmutableArray<RecentFile> others = [.. Recent.Where(recent => !SamePath(recent.Path, full))];
        return this with { RecentFiles = [new RecentFile(full, when, isFinalCopy), .. others.Take(MaxRecentFiles - 1)] };
    }

    private static readonly Lock s_lock = new();

    private AppSettings Validated() => this with
    {
        RecoveryDays = RecoveryDayChoices.Contains(RecoveryDays) ? RecoveryDays : DefaultRecoveryDays,
        Appearance = Enum.IsDefined(Appearance) ? Appearance : AppearanceChoice.MatchSystem,
        DateFormat = DateFormat is { } format && !Enum.IsDefined(format) ? null : DateFormat,
        Language = Localization.Languages.IsSupported(Language) ? Language : null,
        PagesPanelWidth = double.IsFinite(PagesPanelWidth) ? Math.Clamp(PagesPanelWidth, MinPagesPanelWidth, MaxPagesPanelWidth) : DefaultPagesPanelWidth,
        RecentFiles = [.. Recent.Where(recent => !string.IsNullOrWhiteSpace(recent.Path)).Take(MaxRecentFiles)],
    };

    internal static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
