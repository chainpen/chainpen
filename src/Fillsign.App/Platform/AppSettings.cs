using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fillsign.App.Platform;

/// <summary>
/// Small app preferences kept in the OS app data folder. Phase 2 needs only the launch count (first-launch screen,
/// FR-1.8) and whether the first-run tips were shown. Unreadable settings fall back to defaults.
/// </summary>
public sealed record AppSettings(int LaunchCount = 0, bool HasSeenFirstRunTips = false)
{
    public static string Folder
    {
        get
        {
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

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
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
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Settings could not be saved. {ex.Message}");
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
