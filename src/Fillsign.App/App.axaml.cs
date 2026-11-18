using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
        // The appearance follows Settings in every window, straight away (FR-10.1).
        ApplyAppearance(AppSettings.Load().Appearance);
        AppSettings.Changed += changed => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyAppearance(changed.Appearance));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            AppSettings settings = AppSettings.Update(current => current with { LaunchCount = current.LaunchCount + 1 });
            bool isFirstLaunch = settings.LaunchCount == 1;

            // Recovery copies are kept for the chosen number of days (FR-8.12).
            RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(settings.RecoveryDays), DateTimeOffset.Now);

            // Files passed on the command line open straight away (FS-200).
            string[] paths = [.. (desktop.Args ?? []).Where(File.Exists)];
            desktop.MainWindow = new MainWindow(isFirstLaunch, paths);

            // Files opened from Finder or Explorer while Fillsign is running ("Open with", FS-705).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs files && desktop.MainWindow is MainWindow window)
                    {
                        string[] opened = [.. files.Files.Select(file => file.TryGetLocalPath()).OfType<string>()];
                        _ = window.OpenPathsAsync(opened);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Match system, light or dark (FR-10.1). The page is never recoloured (NFR-10).</summary>
    public void ApplyAppearance(AppearanceChoice choice) => RequestedThemeVariant = choice switch
    {
        AppearanceChoice.Light => Avalonia.Styling.ThemeVariant.Light,
        AppearanceChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
        _ => Avalonia.Styling.ThemeVariant.Default,
    };
}
