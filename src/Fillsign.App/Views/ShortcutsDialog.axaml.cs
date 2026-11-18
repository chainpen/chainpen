using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fillsign.App.Views;

public partial class ShortcutsDialog : UserControl
{
    private readonly TaskCompletionSource _closed = new();

    public ShortcutsDialog()
    {
        InitializeComponent();
        PlatformNote.Text = OperatingSystem.IsMacOS() ? "Shown for this Mac: ⌘ is the Command key." : "On a Mac, Ctrl is ⌘.";
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => CloseButton.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _closed.TrySetResult();
                e.Handled = true;
            }
        };
    }

    public Task Closed => _closed.Task;

    private void OnClose(object? sender, RoutedEventArgs e) => _closed.TrySetResult();
}
