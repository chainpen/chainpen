using Avalonia;

namespace Fillsign.App;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack's install, update and uninstall hooks run first and may exit (FS-804). On Windows they also add and
        // remove "Open with Fillsign" (FS-717).
        Velopack.VelopackApp app = Velopack.VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            app = app
                .OnAfterInstallFastCallback(_ => RegisterFileTypes())
                .OnAfterUpdateFastCallback(_ => RegisterFileTypes())
                .OnBeforeUninstallFastCallback(_ =>
                {
                    if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
                    {
                        Platform.FileAssociations.UnregisterOnWindows(exe);
                    }
                });
        }

        app.Run();

        // For diagnosing problems: FILLSIGN_TRACE_FILE=/path/log.txt writes the app's error log to that file.
        if (Environment.GetEnvironmentVariable("FILLSIGN_TRACE_FILE") is { Length: > 0 } traceFile)
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(traceFile));
            System.Diagnostics.Trace.AutoFlush = true;
        }

        // The interface language is set before any window or text exists; Settings can change it later.
        Localization.Languages.Apply(Platform.AppSettings.Load().Language);
        Platform.FileAssociations.KeepLinuxDesktopEntry();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RegisterFileTypes()
    {
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe)
        {
            Platform.FileAssociations.RegisterOnWindows(exe);
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
