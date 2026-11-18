using Avalonia.Controls;
using Avalonia.Interactivity;
using Fillsign.App.Platform;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class StartView : UserControl
{
    public StartView()
    {
        InitializeComponent();
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

    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDismissProblem(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartViewModel start)
        {
            start.Problem = null;
        }
    }
}
