using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fillsign.App.Localization;

namespace Fillsign.App.Views;

/// <summary>
/// Asks where pages should go (FR-6.4): at the start, after a page number the user types, or at the end. The answer is
/// a gap: 0 is the start, and gap n sits after page n.
/// </summary>
public partial class MoveToDialog : UserControl
{
    private readonly TaskCompletionSource<int?> _result = new();
    private readonly int _pageCount;

    public MoveToDialog()
        : this(1, 2)
    {
    }

    public MoveToDialog(int count, int pageCount)
    {
        InitializeComponent();
        _pageCount = pageCount;
        Heading.Text = Text.Plural(count, Strings.MoveTo_MovePages_One, Strings.MoveTo_MovePages_Other);
        OfPages.Text = Text.Format(Strings.MoveTo_OfPages, pageCount);
        AtEndText.Text = Text.Format(Strings.MoveTo_AtTheEndAfterPage, pageCount);

        // Typing a number means "after that page", so the choice follows the typing.
        PageNumber.GotFocus += (_, _) => AfterPage.IsChecked = true;
        PageNumber.TextChanged += (_, _) =>
        {
            AfterPage.IsChecked = true;
            ShowError(null);
        };
        PageNumber.KeyDown += OnKeyDown;
        AfterPage.IsCheckedChanged += (_, _) =>
        {
            if (AfterPage.IsChecked == true && !PageNumber.IsFocused)
            {
                PageNumber.Focus();
            }
        };

        AttachedToVisualTree += (_, _) => AtEnd.Focus();
        KeyDown += OnKeyDown;
    }

    /// <summary>The chosen gap, or null when cancelled.</summary>
    public Task<int?> Result => _result.Task;

    /// <summary>Chooses a gap as the user would: the start, the end, or a typed page number. For tests.</summary>
    internal void Choose(int gap)
    {
        if (gap == 0)
        {
            AtStart.IsChecked = true;
        }
        else if (gap == _pageCount)
        {
            AtEnd.IsChecked = true;
        }
        else
        {
            AfterPage.IsChecked = true;
            PageNumber.Text = gap.ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The gap the choices describe, or null with a message when the typed page number isn't a page.</summary>
    private int? ChosenGap(out string? problem)
    {
        problem = null;
        if (AtStart.IsChecked == true)
        {
            return 0;
        }

        if (AtEnd.IsChecked == true)
        {
            return _pageCount;
        }

        string typed = PageNumber.Text?.Trim() ?? string.Empty;
        if (int.TryParse(typed, NumberStyles.None, CultureInfo.CurrentCulture, out int page) && page >= 1 && page <= _pageCount)
        {
            return page;
        }

        problem = typed.Length == 0 ? Strings.MoveTo_TypeAPageNumber : Text.Format(Strings.MoveTo_TypeAPageNumberFrom1To, _pageCount);
        return null;
    }

    private void ShowError(string? message)
    {
        Error.IsVisible = message is not null;
        ErrorText.Text = message;
        PageNumber.Classes.Set("error", message is not null);
    }

    private void OnMove(object? sender, RoutedEventArgs e)
    {
        if (ChosenGap(out string? problem) is { } gap)
        {
            _result.TrySetResult(gap);
            return;
        }

        ShowError(problem);
        PageNumber.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _result.TrySetResult(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnMove(sender, e);
            e.Handled = true;
        }
    }
}
