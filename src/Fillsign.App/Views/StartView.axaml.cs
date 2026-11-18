using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void OnOpen(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnPracticeForm(object? sender, RoutedEventArgs e) => PracticeFormRequested?.Invoke(this, EventArgs.Empty);

    private void OnDismissProblem(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartViewModel start)
        {
            start.Problem = null;
        }
    }
}
