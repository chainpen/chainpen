using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Fillsign.App.Platform;
using Fillsign.App.Views;

namespace Fillsign.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            AppSettings settings = AppSettings.Load();
            bool isFirstLaunch = settings.LaunchCount == 0;
            (settings with { LaunchCount = settings.LaunchCount + 1 }).Save();

            // Files passed on the command line open straight away (FS-200).
            string[] paths = [.. (desktop.Args ?? []).Where(File.Exists)];
            desktop.MainWindow = new MainWindow(isFirstLaunch, paths);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
