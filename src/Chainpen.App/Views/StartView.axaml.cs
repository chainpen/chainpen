using Avalonia.Controls;
using Avalonia.Interactivity;
using Chainpen.App.Platform;
using Chainpen.App.ViewModels;

namespace Chainpen.App.Views;

public partial class StartView : UserControl
{
    public StartView()
    {
        InitializeComponent();
        TitleBar.ActsAsTitleBar(TitleStrip);
    }

    /// <summary>Raised when the person asks to open files; the window shows the picker.</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? PracticeFormRequested;

    /// <summary>A Recent row was chosen: open its file, or recover its unsaved work.</summary>
    public event Action<RecentItemViewModel>? RecentRequested;

    /// <summary>"Recover changes" on the unexpected-shutdown banner.</summary>
    public event Action<RecoveryCopy>? RecoverRequested;

    public event EventHandler? SettingsRequested;

    private void OnOpen(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnPracticeForm(object? sender, RoutedEventArgs e) => PracticeFormRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenRecent(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecentItemViewModel item })
        {
            RecentRequested?.Invoke(item);
        }
    }

    private void OnRecover(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartViewModel { Recovery: { } copy })
        {
            RecoverRequested?.Invoke(copy);
        }
    }

    private void OnNotNow(object? sender, RoutedEventArgs e) => (DataContext as StartViewModel)?.PutOffRecovery();

    private void OnClearRecent(object? sender, RoutedEventArgs e) => (DataContext as StartViewModel)?.ClearRecent();

    private void OnCrashReportsYes(object? sender, RoutedEventArgs e) => (DataContext as StartViewModel)?.AnswerCrashReports(isYes: true);

    private void OnCrashReportsNo(object? sender, RoutedEventArgs e) => (DataContext as StartViewModel)?.AnswerCrashReports(isYes: false);

    private void OnDeleteCrashReports(object? sender, RoutedEventArgs e) => (DataContext as StartViewModel)?.DeleteCrashReports();

    /// <summary>Opens the report as a GitHub issue in the browser, where the person reads it and decides to send it.</summary>
    private async void OnSendCrashReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartViewModel start && start.TakeCrashReportLink() is { } link && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(link).ConfigureAwait(true);
        }
    }

    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDismissProblem(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartViewModel start)
        {
            start.Problem = null;
        }
    }
}
