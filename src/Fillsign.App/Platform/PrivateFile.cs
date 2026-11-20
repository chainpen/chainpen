namespace Fillsign.App.Platform;

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
        string temporary = path + ".tmp";
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
    }
}
