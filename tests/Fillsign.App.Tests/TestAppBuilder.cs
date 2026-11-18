using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Fillsign.App.Tests.TestAppBuilder))]

namespace Fillsign.App.Tests;

/// <summary>Runs the real app with Skia rendering but no window system, so frames can be captured.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Settings, recent files and recovery copies go to a folder of the test run's own, never the person's.
        Platform.AppSettings.FolderOverride = Path.Combine(Path.GetTempPath(), "fillsign-tests", "app-data-" + Environment.ProcessId);
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
