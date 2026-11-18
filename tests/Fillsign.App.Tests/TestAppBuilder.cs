using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Fillsign.App.Tests.TestAppBuilder))]

namespace Fillsign.App.Tests;

/// <summary>Runs the real app with Skia rendering but no window system, so frames can be captured.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
