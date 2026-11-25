using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chainpen.App.Platform;

/// <summary>
/// One recovery copy (FR-8.12): unsaved work, kept automatically in the app data folder, never next to the document.
/// </summary>
/// <param name="Id">Names the copy's files.</param>
/// <param name="Name">The document's name as shown in the window ("lease-renewal.pdf").</param>
/// <param name="WorkingCopyPath">Where the working copy was last saved, so recovering and saving goes back there.</param>
/// <param name="OpenedFrom">The file the document was opened from, to match it in Recent.</param>
/// <param name="SavedAt">When the copy was written.</param>
/// <param name="IsDismissed">The person chose "Not now": it stays recoverable from Recent but isn't offered again.</param>
/// <param name="IsForUpdate">Kept on purpose before restarting to update, rather than left by a crash.</param>
/// <param name="OwnerProcessId">
/// The Chainpen process that wrote the copy, with <paramref name="OwnerStartedAt"/> (process numbers are reused). While
/// that process is running the copy belongs to a document open in it, and isn't anyone else's to offer or remove.
/// Copies from before these were recorded have neither, and count as left behind.
/// </param>
/// <param name="OwnerStartedAt">When the owning process started.</param>
public sealed record RecoveryCopy(
    Guid Id,
    string Name,
    string? WorkingCopyPath,
    string? OpenedFrom,
    DateTimeOffset SavedAt,
    bool IsDismissed = false,
    bool IsForUpdate = false,
    int OwnerProcessId = 0,
    DateTimeOffset? OwnerStartedAt = null);

/// <summary>
/// Recovery copies in the app data folder (FS-704): each is a working copy (so recovering restores every item) plus a
/// small description. A window writes its copy every 30 seconds while it has unsaved changes, and removes it once the
/// work is saved or deliberately discarded, so copies left at start-up mean Chainpen closed unexpectedly.
/// </summary>
public static class RecoveryStore
{
    public static string Folder => Path.Combine(AppSettings.Folder, "Recovery");

    // Copies that belong to documents open in this process. Another window's start screen, or a second Chainpen
    // running beside this one, would otherwise offer a live document's copy as work left by a crash; recovering it
    // gave two documents one copy, and closing either removed the other's.
    private static readonly HashSet<Guid> s_openHere = [];

    private static readonly DateTimeOffset s_started = StartOfThisProcess();

    private static DateTimeOffset StartOfThisProcess()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>A document open here now has this copy: it isn't offered for recovery while it does.</summary>
    public static void Hold(Guid id)
    {
        lock (s_openHere)
        {
            s_openHere.Add(id);
        }
    }

    /// <summary>The document that had this copy has closed. A copy still on disk is then work left behind.</summary>
    public static void Release(Guid id)
    {
        lock (s_openHere)
        {
            s_openHere.Remove(id);
        }
    }

    /// <summary>Whether a document open now, here or in another running Chainpen, has this copy.</summary>
    public static bool IsInUse(RecoveryCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        lock (s_openHere)
        {
            if (s_openHere.Contains(copy.Id))
            {
                return true;
            }
        }

        return IsHeldElsewhere(copy);
    }

    /// <summary>Whether another Chainpen process that is still running wrote this copy.</summary>
    public static bool IsHeldElsewhere(RecoveryCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (copy.OwnerProcessId == 0 || copy.OwnerProcessId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var owner = System.Diagnostics.Process.GetProcessById(copy.OwnerProcessId);

            // The number alone isn't enough: after a crash or a restart another program can have it.
            return !owner.HasExited
                && (copy.OwnerStartedAt is not { } started || (owner.StartTime.ToUniversalTime() - started.UtcDateTime).Duration() < TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // No such process, or one this user can't look at, which isn't this user's Chainpen.
            return false;
        }
    }

    /// <summary>
    /// A document opened from a copy takes it over (Recover changes): the copy is marked as this process's, so another
    /// running Chainpen stops offering it.
    /// </summary>
    public static void Claim(RecoveryCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        Hold(copy.Id);
        try
        {
            PrivateFile.WriteReplacing(InfoPath(copy.Id), JsonSerializer.SerializeToUtf8Bytes(Owned(copy), RecoveryJson.Default.RecoveryCopy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be updated. {ex.Message}");
        }
    }

    private static RecoveryCopy Owned(RecoveryCopy copy) => copy with { OwnerProcessId = Environment.ProcessId, OwnerStartedAt = s_started };

    /// <summary>
    /// Writes or replaces a copy, recorded as this process's. Failing to write one isn't worth interrupting anyone
    /// over.
    /// </summary>
    public static bool Write(RecoveryCopy copy, byte[] workingCopy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        try
        {
            Directory.CreateDirectory(Folder);
            PrivateFile.WriteReplacing(PdfPath(copy.Id), workingCopy);
            PrivateFile.WriteReplacing(InfoPath(copy.Id), JsonSerializer.SerializeToUtf8Bytes(Owned(copy), RecoveryJson.Default.RecoveryCopy));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller says no copy was kept rather than claiming one was.
            System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be written. {ex.Message}");
            return false;
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
                PrivateFile.WriteReplacing(InfoPath(id), JsonSerializer.SerializeToUtf8Bytes(copy with { IsDismissed = true }, RecoveryJson.Default.RecoveryCopy));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"A recovery copy could not be updated. {ex.Message}");
            }
        }
    }

    public static void Delete(Guid id)
    {
        Release(id);
        TryDelete(PdfPath(id));
        TryDelete(InfoPath(id));
    }

    /// <summary>
    /// Raised after Settings deletes copies, so documents still open write theirs again straight away rather than being
    /// left without one until their next change.
    /// </summary>
    public static event Action? CopiesDeleted;

    /// <summary>
    /// Whether the file at <paramref name="path"/> has been written since the copy was kept. The file then holds
    /// newer work than the copy does, so the copy mustn't stand in for it or be saved back over it.
    /// </summary>
    public static bool IsOlderThanFile(RecoveryCopy copy, string path)
    {
        ArgumentNullException.ThrowIfNull(copy);
        try
        {
            return File.Exists(path) && new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) > copy.SavedAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>"Delete all recovery copies" in Settings (FR-10.1).</summary>
    public static void DeleteAll()
    {
        // A copy another running Chainpen holds is its open document's only one, and that Chainpen wouldn't know to
        // write it again as the documents open here do.
        HashSet<string> heldElsewhere = [.. List().Where(IsHeldElsewhere).Select(copy => copy.Id.ToString("N"))];

        // Every other file, not just the copies List() can read: a stray one holds document contents too.
        foreach (string file in Files())
        {
            if (!heldElsewhere.Contains(Path.GetFileName(file).Split('.')[0]))
            {
                TryDelete(file);
            }
        }

        CopiesDeleted?.Invoke();
    }

    /// <summary>Removes copies older than the kept period (FR-8.12: 30 days by default).</summary>
    public static void DeleteOlderThan(TimeSpan age, DateTimeOffset now)
    {
        // A copy another running Chainpen holds is its document's only one, however long it has gone unchanged.
        IReadOnlyList<RecoveryCopy> copies = List();
        foreach (RecoveryCopy copy in copies.Where(copy => now - copy.SavedAt > age && !IsHeldElsewhere(copy)))
        {
            Delete(copy.Id);
        }

        // Files no readable description accounts for: a copy whose description was never written or is damaged, or a
        // half-written file. They have no saved time, so their age is when they were last written.
        HashSet<string> described = [.. copies.Select(copy => copy.Id.ToString("N"))];
        foreach (string file in Files())
        {
            bool isDescribed = described.Contains(Path.GetFileName(file).Split('.')[0]);
            if (!isDescribed && now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)) > age)
            {
                TryDelete(file);
            }
        }

        CopiesDeleted?.Invoke();
    }

    private static IReadOnlyList<string> Files()
    {
        try
        {
            return Directory.Exists(Folder) ? [.. Directory.EnumerateFiles(Folder)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"The recovery copies folder could not be read. {ex.Message}");
            return [];
        }
    }

    private static string PdfPath(Guid id) => Path.Combine(Folder, $"{id:N}.pdf");

    private static string InfoPath(Guid id) => Path.Combine(Folder, $"{id:N}.json");
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
