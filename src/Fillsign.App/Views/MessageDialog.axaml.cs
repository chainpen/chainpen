using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fillsign.App.Views;

/// <summary>Whether a message is about a problem (red warning icon) or just information (no icon).</summary>
public enum MessageTone
{
    Problem,
    Information,
}

public partial class MessageDialog : UserControl
{
    private readonly TaskCompletionSource _closed = new();

    public MessageDialog()
        : this(string.Empty, [], "Close")
    {
    }

    /// <param name="closeLabel">Names what closing does ("Got it", "Close"); never "OK".</param>
    /// <param name="tone">Information (About, "you're up to date") has no warning icon, so it doesn't read as an error.</param>
    public MessageDialog(string title, IReadOnlyList<string> paragraphs, string closeLabel, MessageTone tone = MessageTone.Problem)
    {
        InitializeComponent();
        Heading.Text = title;
        Icon.IsVisible = tone == MessageTone.Problem;
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
