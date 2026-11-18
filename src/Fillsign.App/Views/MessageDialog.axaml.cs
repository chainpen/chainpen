using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fillsign.App.Views;

public partial class MessageDialog : UserControl
{
    private readonly TaskCompletionSource _closed = new();

    public MessageDialog()
        : this(string.Empty, [], "Close")
    {
    }

    /// <param name="closeLabel">Names what closing does ("Got it", "Close"); never "OK".</param>
    public MessageDialog(string title, IReadOnlyList<string> paragraphs, string closeLabel)
    {
        InitializeComponent();
        Heading.Text = title;
        foreach (string paragraph in paragraphs)
        {
            Lines.Children.Add(new TextBlock { Text = paragraph, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        OkButton.Content = closeLabel;
        AttachedToVisualTree += (_, _) => OkButton.Focus();
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
