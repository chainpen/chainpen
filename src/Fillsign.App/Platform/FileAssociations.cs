using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Fillsign.App.Platform;

/// <summary>
/// "Open with Fillsign" for PDFs and pictures (FS-717). macOS reads the document types from the app bundle's
/// Info.plist. On Windows the installer's hooks register Fillsign for the current user, as an option rather than the
/// default (Windows asks people to choose that themselves), and remove it on uninstall. A Linux AppImage isn't
/// installed, so each run keeps a desktop entry pointing at it.
/// </summary>
public static partial class FileAssociations
{
    private const string ProgId = "Fillsign.Document";
    private static readonly string[] s_extensions = [".pdf", ".png", ".jpg", ".jpeg", ".heic"];

    /// <summary>Velopack's after-install and after-update hook on Windows.</summary>
    [SupportedOSPlatform("windows")]
    public static void RegisterOnWindows(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        using (RegistryKey prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(string.Empty, "Document for Fillsign");
            using RegistryKey icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue(string.Empty, $"\"{exePath}\",0");
            using RegistryKey command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }

        foreach (string extension in s_extensions)
        {
            using RegistryKey openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (RegistryKey app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(exePath)}"))
        {
            app.SetValue("FriendlyAppName", "Fillsign");
            using RegistryKey types = app.CreateSubKey("SupportedTypes");
            foreach (string extension in s_extensions)
            {
                types.SetValue(extension, string.Empty);
            }
        }

        AssociationsChanged();
    }

    /// <summary>Velopack's before-uninstall hook on Windows: takes back exactly what <see cref="RegisterOnWindows"/> added.</summary>
    [SupportedOSPlatform("windows")]
    public static void UnregisterOnWindows(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{Path.GetFileName(exePath)}", throwOnMissingSubKey: false);
        foreach (string extension in s_extensions)
        {
            using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        AssociationsChanged();
    }

    /// <summary>
    /// On Linux, when running as an AppImage, writes (or refreshes) the desktop entry that lists Fillsign under
    /// "Open with" for PDFs and pictures. Does nothing elsewhere.
    /// </summary>
    public static void KeepLinuxDesktopEntry()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage)
        {
            return;
        }

        try
        {
            string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string folder = Path.Combine(string.IsNullOrEmpty(dataHome) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : dataHome, "applications");
            if (DesktopEntry(appImage) is not { } entry)
            {
                System.Diagnostics.Trace.TraceWarning("The AppImage's path has characters a desktop entry can't hold; no Open with entry.");
                return;
            }

            string path = Path.Combine(folder, "fillsign.desktop");
            if (File.Exists(path) && File.ReadAllText(path) == entry)
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(path, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without it Fillsign still runs; it just isn't offered under "Open with".
            System.Diagnostics.Trace.TraceWarning($"The desktop entry couldn't be written. {ex.Message}");
        }
    }

    /// <summary>
    /// The freedesktop.org entry: Fillsign for PDFs and pictures, opening whatever files it's given. Null for a path a
    /// desktop entry can't hold (a line break would start a new key).
    /// </summary>
    internal static string? DesktopEntry(string appImage) =>
        appImage.Any(char.IsControl)
            ? null
            : "[Desktop Entry]\n"
              + "Type=Application\n"
              + "Name=Fillsign\n"
              + "Comment=Fill in and sign PDF forms\n"
              + $"Exec={ExecArgument(appImage)} %F\n"
              + "Terminal=false\n"
              + "Categories=Office;Viewer;\n"
              + "MimeType=application/pdf;image/png;image/jpeg;image/heic;\n";

    /// <summary>
    /// A path as one Exec argument, by the Desktop Entry Specification's rules: quoted, with " ` $ and \ escaped
    /// inside the quotes and % written %%; then, because Exec is a string value, every backslash doubled again.
    /// </summary>
    private static string ExecArgument(string path)
    {
        var quoted = new System.Text.StringBuilder("\"");
        foreach (char character in path)
        {
            if (character is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(character == '%' ? "%%" : character.ToString());
        }

        return quoted.Append('"').ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    private static void AssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
