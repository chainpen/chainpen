using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Chainpen.App.Localization;

namespace Chainpen.App.Views;

/// <summary>The answer to a <see cref="ChoiceDialog"/>.</summary>
public enum Choice
{
    Cancel,
    Main,
    Other,
}

/// <summary>
/// A question with a main answer, an optional other answer and Cancel (D4 close with unsaved changes, D8 restore).
/// Esc cancels; Enter gives the focused button, which starts as <paramref name="focusMain"/> decides.
/// </summary>
public partial class ChoiceDialog : UserControl
{
    private readonly TaskCompletionSource<Choice> _result = new();

    public ChoiceDialog()
        : this("Question", "Details", "Yes", null, focusMain: true)
    {
    }

    /// <param name="title">The question, as the dialog's heading.</param>
    /// <param name="body">What happens either way, in a sentence or two.</param>
    /// <param name="mainLabel">The main button names what it does ("Save", "Restore edits"); never "OK".</param>
    /// <param name="otherLabel">A second answer on the left ("Don’t save"), or null for none.</param>
    /// <param name="focusMain">Start on the main button when it's the safe choice; otherwise on Cancel.</param>
    /// <param name="isMainDestructive">Shows the main button in red, for answers that discard something.</param>
    /// <param name="cancelLabel">Names what backing out does when "Cancel" would be unclear ("Don’t open").</param>
    public ChoiceDialog(string title, string body, string mainLabel, string? otherLabel, bool focusMain, bool isMainDestructive = false, string? cancelLabel = null)
    {
        InitializeComponent();
        CancelButton.Content = cancelLabel ?? Strings.Common_Cancel;
        Heading.Text = title;
        Body.Text = body;
        MainButton.Content = mainLabel;
        MainButton.Classes.Set("primary", !isMainDestructive);
        MainButton.Classes.Set("danger-fill", isMainDestructive);
        OtherButton.Content = otherLabel;
        OtherButton.IsVisible = otherLabel is not null;
        // Focus once laid out: while the dialog layer is still appearing, nothing in it can take focus.
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => (focusMain ? MainButton : CancelButton).Focus(NavigationMethod.Tab),
            Avalonia.Threading.DispatcherPriority.Loaded);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result.TrySetResult(Choice.Cancel);
                e.Handled = true;
            }
        };
    }

    public Task<Choice> Result => _result.Task;

    private void OnMain(object? sender, RoutedEventArgs e) => _result.TrySetResult(Choice.Main);

    private void OnOther(object? sender, RoutedEventArgs e) => _result.TrySetResult(Choice.Other);

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(Choice.Cancel);
}
