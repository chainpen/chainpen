using Fillsign.App.Platform;
using Microsoft.Win32;

namespace Fillsign.App.Tests;

/// <summary>FS-717: "Open with Fillsign" for PDFs and pictures.</summary>
public sealed class FileAssociationTests
{
    [Fact]
    public void The_Linux_desktop_entry_opens_files_with_the_AppImage_and_lists_PDFs_and_pictures()
    {
        string? entry = FileAssociations.DesktopEntry("/home/someone/Apps/Fillsign.AppImage");
        Assert.Contains("Exec=\"/home/someone/Apps/Fillsign.AppImage\" %F", entry, StringComparison.Ordinal);
        Assert.Contains("MimeType=application/pdf;image/png;image/jpeg;image/heic;", entry, StringComparison.Ordinal);

        // Characters the launcher would otherwise read as its own: quoted, escaped, and backslashes doubled for the file.
        Assert.Contains("Exec=\"/home/a \\\\\"b\\\\$c\\\\`d\\\\\\\\e 100%%.AppImage\" %F", FileAssociations.DesktopEntry("/home/a \"b$c`d\\e 100%.AppImage"), StringComparison.Ordinal);

        // A line break would add keys of its own.
        Assert.Null(FileAssociations.DesktopEntry("/home/someone/x\nExec=evil"));
    }

    [Fact]
    public void Windows_registration_adds_an_Open_with_entry_and_uninstalling_takes_it_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string exe = @"C:\Test\Fillsign.App.exe";
        FileAssociations.RegisterOnWindows(exe);
        try
        {
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Fillsign.Document\shell\open\command");
            Assert.Equal($"\"{exe}\" \"%1\"", command?.GetValue(string.Empty));
            using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids");
            Assert.Contains("Fillsign.Document", openWith?.GetValueNames() ?? []);
        }
        finally
        {
            FileAssociations.UnregisterOnWindows(exe);
        }

        Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Classes\Fillsign.Document"));
        using RegistryKey? after = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids");
        Assert.DoesNotContain("Fillsign.Document", after?.GetValueNames() ?? []);
    }
}
