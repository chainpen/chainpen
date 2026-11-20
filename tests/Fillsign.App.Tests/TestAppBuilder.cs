using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Fillsign.App.Tests.TestAppBuilder))]

// One test class at a time: they share the app's settings file, recovery folder and theme, so one class resetting the
// appearance could undo another's dark mode mid-test. The UI tests queue on one UI thread anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
