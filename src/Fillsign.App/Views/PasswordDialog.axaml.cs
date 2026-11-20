using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Fillsign.App.Views;

public partial class PasswordDialog : UserControl
{
    private readonly TaskCompletionSource<string?> _result = new();

    public PasswordDialog()
        : this(string.Empty, false)
    {
    }

    public PasswordDialog(string fileName, bool wasWrong)
    {
        InitializeComponent();
        Heading.Text = $"{fileName} needs a password to open";
        Error.IsVisible = wasWrong;
        Password.Classes.Set("error", wasWrong);

        AttachedToVisualTree += (_, _) => Password.Focus();
    }

    /// <summary>The password, or null if the person cancelled (that file is skipped).</summary>
    public Task<string?> Result => _result.Task;

    private void OnOpen(object? sender, RoutedEventArgs e) => _result.TrySetResult(Password.Text ?? string.Empty);

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOpen(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }

    private void OnToggleShow(object? sender, RoutedEventArgs e)
    {
        bool isHidden = Password.PasswordChar != '\0';
        Password.PasswordChar = isHidden ? '\0' : '•';
        ShowText.Text = isHidden ? "Hide" : "Show";
        ShowIcon.Data = (Geometry?)this.FindResource(isHidden ? "IconEyeClosed" : "IconEye");
        AutomationProperties.SetName(ShowButton, isHidden ? "Hide password" : "Show password");
    }
}
