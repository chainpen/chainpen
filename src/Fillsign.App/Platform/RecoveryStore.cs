using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fillsign.App.Platform;

/// <summary>
/// One recovery copy (FR-8.12): unsaved work, kept automatically in the app data folder, never next to the document.
/// </summary>
/// <param name="Id">Names the copy's files.</param>
/// <param name="Name">The document's name as shown in the window ("lease-renewal.pdf").</param>
/// <param name="WorkingCopyPath">Where the working copy was last saved, so recovering and saving goes back there.</param>
/// <param name="OpenedFrom">The file the document was opened from, to match it in Recent.</param>
/// <param name="SavedAt">When the copy was written.</param>
/// <param name="IsDismissed">The person chose "Not now": it stays recoverable from Recent but isn't offered again.</param>
public sealed record RecoveryCopy(Guid Id, string Name, string? WorkingCopyPath, string? OpenedFrom, DateTimeOffset SavedAt, bool IsDismissed = false);

/// <summary>
/// Recovery copies in the app data folder (FS-704): each is a working copy (so recovering restores every item) plus a
/// small description. A window writes its copy every 30 seconds while it has unsaved changes, and removes it once the
/// work is saved or deliberately discarded, so copies left at start-up mean Fillsign closed unexpectedly.
/// </summary>
public static class RecoveryStore
{
    public static string Folder => Path.Combine(AppSettings.Folder, "Recovery");

    /// <summary>Writes or replaces a copy. Failing to write one isn't worth interrupting anyone over.</summary>
    public static void Write(RecoveryCopy copy, byte[] workingCopy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        try
        {
            Directory.CreateDirectory(Folder);
            WriteReplacing(PdfPath(copy.Id), workingCopy);
            WriteReplacing(InfoPath(copy.Id), JsonSerializer.SerializeToUtf8Bytes(copy, RecoveryJson.Default.RecoveryCopy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be written. {ex.Message}");
        }
    }

    /// <summary>Every readable copy, newest first. Unreadable ones are skipped.</summary>
    public static IReadOnlyList<RecoveryCopy> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var copies = new List<RecoveryCopy>();
        foreach (string info in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(info), RecoveryJson.Default.RecoveryCopy) is { } copy && File.Exists(PdfPath(copy.Id)))
                {
                    copies.Add(copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Trace.TraceWarning($"A recovery copy's description could not be read. {ex.Message}");
            }
        }

        return [.. copies.OrderByDescending(copy => copy.SavedAt)];
    }

    public static byte[] Read(Guid id) => File.ReadAllBytes(PdfPath(id));

    /// <summary>"Not now": keep it recoverable from Recent without offering it again.</summary>
    public static void Dismiss(Guid id)
    {
        if (List().FirstOrDefault(copy => copy.Id == id) is { } copy)
        {
            try
            {
                WriteReplacing(InfoPath(id), JsonSerializer.SerializeToUtf8Bytes(copy with { IsDismissed = true }, RecoveryJson.Default.RecoveryCopy));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be updated. {ex.Message}");
            }
        }
    }

    public static void Delete(Guid id)
    {
        TryDelete(PdfPath(id));
        TryDelete(InfoPath(id));
    }

    /// <summary>"Delete all recovery copies" in Settings (FR-10.1).</summary>
    public static void DeleteAll()
    {
        foreach (RecoveryCopy copy in List())
        {
            Delete(copy.Id);
        }
    }

    /// <summary>Removes copies older than the kept period (FR-8.12: 7 days by default).</summary>
    public static void DeleteOlderThan(TimeSpan age, DateTimeOffset now)
    {
        foreach (RecoveryCopy copy in List().Where(copy => now - copy.SavedAt > age))
        {
            Delete(copy.Id);
        }
    }

    private static string PdfPath(Guid id) => Path.Combine(Folder, $"{id:N}.pdf");

    private static string InfoPath(Guid id) => Path.Combine(Folder, $"{id:N}.json");

    /// <summary>Owner-only, written beside and swapped in, so a crash mid-write leaves the previous copy intact.</summary>
    private static void WriteReplacing(string path, byte[] contents)
    {
        string temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(contents);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"A recovery file could not be removed. {ex.Message}");
        }
    }
}

[JsonSerializable(typeof(RecoveryCopy))]
internal sealed partial class RecoveryJson : JsonSerializerContext;
