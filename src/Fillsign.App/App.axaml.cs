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

        // Avalonia reads the app menu once, straight after Initialize, and puts in "About Avalonia" if there's none,
        // so it has to be set here rather than when the first window opens.
        if (OperatingSystem.IsMacOS())
        {
            NativeMenu.SetMenu(this, MacAppMenu(FrontWindow));
        }
    }

    /// <summary>The window in front, which About and update messages open over.</summary>
    private MainWindow? FrontWindow() =>
        ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsActive) ?? desktop.MainWindow as MainWindow
            : null;

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

            // Updates: check quietly once the first window is up; before restarting, keep every window's unsaved work.
            AppUpdates.Instance.KeepUnsavedWork = () => Task.WhenAll(desktop.Windows.OfType<MainWindow>().Select(window => window.KeepForUpdateAsync()));
            Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = AppUpdates.Instance.CheckAsync(), TimeSpan.FromSeconds(10));

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

    /// <summary>
    /// The macOS app menu (the one named Fillsign): About, Settings and Check for Updates sit there, as on every Mac app, rather than
    /// Avalonia's default "About Avalonia". macOS adds Services, Hide and Quit itself.
    /// </summary>
    /// <param name="frontWindow">The window About opens over.</param>
    internal static NativeMenu MacAppMenu(Func<MainWindow?> frontWindow)
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("About Fillsign")
        {
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => frontWindow()?.ShowAbout()),
        });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Settings…")
        {
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(MainWindow.ShowSettings),
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta),
        });
        menu.Items.Add(new NativeMenuItem("Check for Updates…")
        {
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _ = frontWindow()?.CheckForUpdatesAsync()),
        });
        return menu;
    }

    /// <summary>The version people see: the release number, without build details.</summary>
    public static string DisplayVersion
    {
        get
        {
            string? version = typeof(App).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            return version?.Split('+')[0] ?? "unknown";
        }
    }

    /// <summary>Match system, light or dark (FR-10.1). The page is never recoloured (NFR-10).</summary>
    public void ApplyAppearance(AppearanceChoice choice) => RequestedThemeVariant = choice switch
    {
        AppearanceChoice.Light => Avalonia.Styling.ThemeVariant.Light,
        AppearanceChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
        _ => Avalonia.Styling.ThemeVariant.Default,
    };
}
