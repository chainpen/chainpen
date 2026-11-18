using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Fillsign.App.Views;

/// <summary>Asks where pages should go: at the start, after any page, or at the end (FR-6.4).</summary>
public partial class MoveToDialog : UserControl
{
    private readonly TaskCompletionSource<int?> _result = new();

    public MoveToDialog()
        : this(1, 2)
    {
    }

    public MoveToDialog(int count, int pageCount)
    {
        InitializeComponent();
        Heading.Text = count == 1 ? "Move 1 page" : $"Move {count} pages";

        // Gap 0 is the start; gap n sits after page n.
        var choices = new List<string> { "At the start" };
        for (int page = 1; page < pageCount; page++)
        {
            choices.Add($"After page {page}");
        }

        choices.Add($"At the end (after page {pageCount})");
        Positions.ItemsSource = choices;
        Positions.SelectedIndex = pageCount;
        AttachedToVisualTree += (_, _) => Positions.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result.TrySetResult(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>The chosen gap, or null when cancelled.</summary>
    public Task<int?> Result => _result.Task;

    /// <summary>Chooses a gap, as a click on the list would. For tests.</summary>
    internal void Choose(int gap) => Positions.SelectedIndex = gap;

    private void OnMove(object? sender, RoutedEventArgs e) => _result.TrySetResult(Positions.SelectedIndex);

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Positions.IsDropDownOpen)
        {
            OnMove(sender, e);
            e.Handled = true;
        }
    }
}
