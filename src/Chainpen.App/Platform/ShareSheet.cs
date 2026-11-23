using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Chainpen.App.Platform;

/// <summary>
/// Share… on the export notice (FS-613, FR-8.5): the macOS share sheet (Mail, Messages, AirDrop…) with the final copy.
/// Windows and Linux offer Show in folder; their share panels need APIs this cross-platform build doesn't reference.
/// </summary>
public static partial class ShareSheet
{
    public static bool IsAvailable => OperatingSystem.IsMacOS();

    /// <summary>Opens the share sheet for <paramref name="path"/> next to <paramref name="anchor"/>. Returns false when it can't.</summary>
    public static bool Show(string path, Control anchor)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(anchor);
        if (!OperatingSystem.IsMacOS() || TopLevel.GetTopLevel(anchor) is not { } top || !File.Exists(path))
        {
            return false;
        }

        if (top.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle || handle.NSView == 0)
        {
            return false;
        }

        // The sheet points at the button; Avalonia's view has y going down, like the anchor's position here.
        Point? origin = anchor.TranslatePoint(default, top);
        if (origin is not { } at)
        {
            return false;
        }

        return ShowOnMac(path, handle.NSView, new CGRect(at.X, at.Y, anchor.Bounds.Width, anchor.Bounds.Height));
    }

    [SupportedOSPlatform("macos")]
    private static bool ShowOnMac(string path, nint view, CGRect rect)
    {
        nint url = Send(Class("NSURL"), Selector("fileURLWithPath:"), NSString(path));
        nint items = Send(Class("NSArray"), Selector("arrayWithObject:"), url);
        nint picker = Send(Send(Class("NSSharingServicePicker"), Selector("alloc")), Selector("initWithItems:"), items);
        if (picker == 0)
        {
            return false;
        }

        // NSMinYEdge: below the button in a flipped view.
        SendRect(picker, Selector("showRelativeToRect:ofView:preferredEdge:"), rect, view, 1);
        return true;
    }

    private static nint NSString(string text)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendRect(nint receiver, nint selector, CGRect rect, nint view, nint edge);
}
