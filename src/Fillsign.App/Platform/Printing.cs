using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Fillsign.App.Platform;

/// <summary>What happened when printing was asked for.</summary>
public enum PrintOutcome
{
    /// <summary>The system's print dialog opened.</summary>
    DialogShown,

    /// <summary>The file opened in the computer's PDF viewer instead, to print from there.</summary>
    OpenedInViewer,

    Failed,
}

/// <summary>
/// Printing through the system (FS-808). Avalonia has no printing, so the final copy is printed as a PDF: in the macOS
/// print panel, with the default app's Print command on Windows, or opened in the default viewer on Linux.
/// </summary>
public static partial class Printing
{
    /// <summary>Where print copies go. They're the final copy, so they're kept private and cleared after a day.</summary>
    public static string Folder => Path.Combine(AppSettings.Folder, "Print");

    public static PrintOutcome Print(string pdfPath, TopLevel? window)
    {
        ArgumentNullException.ThrowIfNull(pdfPath);
        try
        {
            if (OperatingSystem.IsMacOS() && window?.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle && handle.NSWindow != 0)
            {
                return PrintOnMac(pdfPath, handle.NSWindow) ? PrintOutcome.DialogShown : PrintOutcome.Failed;
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(pdfPath) { UseShellExecute = true, Verb = "print" })?.Dispose();
                    return PrintOutcome.DialogShown;
                }
                catch (Win32Exception)
                {
                    // The default PDF app has no Print command: open it there instead.
                }
            }

            OpenInViewer(pdfPath);
            return PrintOutcome.OpenedInViewer;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning($"Printing couldn't start. {ex.Message}");
            return PrintOutcome.Failed;
        }
    }

    private static void OpenInViewer(string path)
    {
        ProcessStartInfo start = OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open") { UseShellExecute = false } : new ProcessStartInfo(path) { UseShellExecute = true };
        if (OperatingSystem.IsLinux())
        {
            start.ArgumentList.Add(path);
        }

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Writes the copy to print, readable only by this user from the moment it exists: it's a whole filled-in document,
    /// often signed. Returns its path.
    /// </summary>
    public static string WriteCopy(string documentName, DateTime now, byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(documentName)} ({now:HH.mm.ss}).pdf");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(pdf);
        }

        return path;
    }

    /// <summary>
    /// Removes print copies older than a day: by then the print has long finished. Run at start-up and before each
    /// print, so a copy isn't left behind until the next time someone prints.
    /// </summary>
    public static void ClearOldCopies(DateTime now)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(Folder, "*.pdf"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"An old print copy couldn't be removed. {ex.Message}");
            }
        }
    }

    // ----- macOS: PDFKit's print operation, as a sheet on the window -----

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long ScaleDownToFit = 2;

    [SupportedOSPlatform("macos")]
    private static bool PrintOnMac(string path, nint nsWindow)
    {
        NativeLibrary.Load("/System/Library/Frameworks/PDFKit.framework/PDFKit");
        nint utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            nint text = Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
            nint url = Send(Class("NSURL"), Selector("fileURLWithPath:"), text);
            nint document = Send(Send(Class("PDFDocument"), Selector("alloc")), Selector("initWithURL:"), url);
            if (document == 0)
            {
                return false;
            }

            nint info = Send(Class("NSPrintInfo"), Selector("sharedPrintInfo"));
            nint operation = SendPrint(document, Selector("printOperationForPrintInfo:scalingMode:autoRotate:"), info, ScaleDownToFit, 1);
            if (operation == 0)
            {
                return false;
            }

            SendRun(operation, Selector("runOperationModalForWindow:delegate:didRunSelector:contextInfo:"), nsWindow, 0, 0, 0);
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPrint(nint receiver, nint selector, nint printInfo, long scalingMode, byte autoRotate);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendRun(nint receiver, nint selector, nint window, nint delegateObject, nint didRunSelector, nint contextInfo);
}
