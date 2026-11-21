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

    /// <summary>
    /// The window in front, which About, messages and opened files go to: the active one, else any still open. The
    /// lifetime's MainWindow is the first window ever shown and may have closed.
    /// </summary>
    private MainWindow? FrontWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        MainWindow[] open = [.. desktop.Windows.OfType<MainWindow>()];
        return open.FirstOrDefault(window => window.IsActive) ?? open.FirstOrDefault();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CatchUnexpectedErrors();

        // The appearance follows Settings in every window, straight away (FR-10.1).
        ApplyAppearance(AppSettings.Load().Appearance);
        AppSettings.Changed += changed => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyAppearance(changed.Appearance));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            // The date format starts as the system's, chosen once so it doesn't change under the person later.
            AppSettings settings = AppSettings.Update(current => current with
            {
                LaunchCount = current.LaunchCount + 1,
                DateFormat = current.DateFormat ?? DateFormats.FromCulture(System.Globalization.CultureInfo.CurrentCulture),
            });
            bool isFirstLaunch = settings.LaunchCount == 1;

            // Recovery copies are kept for the chosen number of days (FR-8.12).
            RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(settings.RecoveryDays), DateTimeOffset.Now);
            Printing.ClearOldCopies(DateTime.UtcNow);

            // Files passed on the command line open straight away (FS-200).
            string[] paths = [.. (desktop.Args ?? []).Where(File.Exists)];
            desktop.MainWindow = new MainWindow(isFirstLaunch, paths);

            // Updates: check quietly once the first window is up; before restarting, keep every window's unsaved work.
            AppUpdates.Instance.KeepUnsavedWork = async () =>
            {
                MainWindow[] windows = [.. desktop.Windows.OfType<MainWindow>()];
                bool[] kept = await Task.WhenAll(windows.Select(window => window.KeepForUpdateAsync())).ConfigureAwait(true);
                if (!kept.All(isKept => isKept))
                {
                    return false;
                }

                foreach (MainWindow window in windows)
                {
                    window.MarkKeptForUpdate();
                }

                return true;
            };
            Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = AppUpdates.Instance.CheckAsync(), TimeSpan.FromSeconds(10));

            // Files opened from Finder or Explorer while Fillsign is running ("Open with", FS-705).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs files)
                    {
                        return;
                    }

                    string[] opened = [.. files.Files.Select(file => file.TryGetLocalPath()).OfType<string>()];
                    if (FrontWindow() is { } window)
                    {
                        _ = window.OpenPathsAsync(opened);
                    }
                    else
                    {
                        new MainWindow(isFirstLaunch: false, opened).Show();
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The last line of defence: an error nothing else expected is logged and explained instead of closing Fillsign,
    /// so the person can still save. Recovery copies keep running either way. Errors from tasks nobody awaited are
    /// logged too, rather than vanishing.
    /// </summary>
    private void CatchUnexpectedErrors()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Trace.TraceError($"Unexpected error: {e.Exception}");
            CrashReports.Record(e.Exception);
            e.Handled = true;
            if (!_isShowingUnexpectedError && FrontWindow() is { } window)
            {
                _isShowingUnexpectedError = true;
                _ = ShowUnexpectedErrorAsync(window);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Trace.TraceError($"Unexpected error in background work: {e.Exception}");
            CrashReports.Record(e.Exception);
            e.SetObserved();
        };

        // Errors that end the app can't be caught, but they can still be written down first (FS-902).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                CrashReports.Record(exception);
            }
        };
    }

    private bool _isShowingUnexpectedError;

    private async Task ShowUnexpectedErrorAsync(MainWindow window)
    {
        try
        {
            await window.ShowMessageAsync(
                "Something went wrong",
                "Fillsign hit a problem it didn’t expect, and what you were doing may not have finished. Your document is still open: save it to be safe, and check that your last change is there.").ConfigureAwait(true);
        }
        finally
        {
            _isShowingUnexpectedError = false;
        }
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
        menu.Items.Add(new NativeMenuItem("Check for updates…")
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
