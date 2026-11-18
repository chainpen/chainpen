using Avalonia;

namespace Fillsign.App;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack's install, update and uninstall hooks run first and may exit (FS-804).
        Velopack.VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
