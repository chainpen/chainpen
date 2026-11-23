namespace Chainpen.App.Platform;

/// <summary>
/// Writes the app's own files that hold personal things (settings with recent document names, recovery copies, saved
/// signatures): readable only by this user from the moment they exist, and replaced whole, so a crash mid-write leaves
/// the previous version rather than half a file.
/// </summary>
internal static class PrivateFile
{
    /// <summary>Writes beside <paramref name="path"/>, owner-only, then swaps it in.</summary>
    public static void WriteReplacing(string path, ReadOnlySpan<byte> contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Named for this process, so two copies of Chainpen writing the same file (settings, most of all) don't write
        // into each other's half-finished one.
        string temporary = $"{path}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        RemoveAbandoned(path);
    }

    /// <summary>
    /// Half-written files a crashed Chainpen left for this path: they hold the same personal things, and with a name
    /// per process nothing would ever write over them.
    /// </summary>
    private static void RemoveAbandoned(string path)
    {
        try
        {
            foreach (string stray in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"))
            {
                // Recent ones may be another running copy's write in progress.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stray) > TimeSpan.FromHours(1))
                {
                    File.Delete(stray);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Old temporary files could not be removed. {ex.Message}");
        }
    }
}
