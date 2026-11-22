using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fillsign.App.Platform;

/// <summary>
/// Whether the person has asked their computer for less motion (macOS Reduce motion, Windows "Show animations" off,
/// GNOME animations off). Fillsign's only animation, the practice guide's bouncing arrow, holds still when it is
/// (WCAG 2.3.3).
/// </summary>
public static partial class ReducedMotion
{
    /// <summary>Tests set this to act as if the setting were on or off; null asks the computer.</summary>
    internal static bool? Override { get; set; }

    // GNOME's setting needs a process to read, so it's read once per run; macOS and Windows are asked each time, so
    // a change in System Settings applies straight away.
    private static readonly Lazy<bool> s_linux = new(ReadGnome);

    /// <summary>Whether motion should be kept to a minimum now.</summary>
    public static bool IsOn
    {
        get
        {
            if (Override is { } overridden)
            {
                return overridden;
            }

            if (OperatingSystem.IsMacOS())
            {
                return ReadMac();
            }

            if (OperatingSystem.IsWindows())
            {
                return ReadWindows();
            }

            return OperatingSystem.IsLinux() && s_linux.Value;
        }
    }

    private static bool ReadMac()
    {
        nint workspace = Send(Class("NSWorkspace"), Selector("sharedWorkspace"));
        return workspace != 0 && SendBool(workspace, Selector("accessibilityDisplayShouldReduceMotion")) != 0;
    }

    private static bool ReadWindows()
    {
        const uint GetClientAreaAnimation = 0x1042;
        return SystemParametersInfo(GetClientAreaAnimation, 0, out int isAnimated, 0) && isAnimated == 0;
    }

    private static bool ReadGnome()
    {
        try
        {
            using var gsettings = Process.Start(new ProcessStartInfo("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (gsettings is null)
            {
                return false;
            }

            string answer = gsettings.StandardOutput.ReadToEnd().Trim();
            gsettings.WaitForExit(2000);
            return answer == "false";
        }
        catch (Win32Exception)
        {
            // No gsettings (not GNOME): nothing says to keep still.
            return false;
        }
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint winIni);
}
