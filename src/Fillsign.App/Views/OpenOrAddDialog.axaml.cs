using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fillsign.App.Localization;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class OpenOrAddDialog : UserControl
{
    private readonly TaskCompletionSource<OpenChoice> _result = new();

    public OpenOrAddDialog()
        : this(Strings.Common_Open, Strings.OpenOrAdd_ThisDocument, 0)
    {
    }

    public OpenOrAddDialog(string title, string documentName, int pageCount)
    {
        InitializeComponent();
        Heading.Text = title;
        Body.Text = Text.Format(Strings.OpenOrAdd_StaysOpenEitherWay, documentName);
        AddDetail.Text = pageCount > 0
            ? Text.Plural(pageCount, Strings.OpenOrAdd_PagesGoAtTheEnd_One, Strings.OpenOrAdd_PagesGoAtTheEnd_Other)
            : Strings.OpenOrAdd_ThePagesGoAtTheEnd;
        AttachedToVisualTree += (_, _) => OpenButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result.TrySetResult(OpenChoice.Cancel);
                e.Handled = true;
            }
        };
    }

    public Task<OpenChoice> Result => _result.Task;

    private void OnOpen(object? sender, RoutedEventArgs e) =>
        _result.TrySetResult(AddPages.IsChecked == true ? OpenChoice.AddPages : OpenChoice.NewWindow);

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(OpenChoice.Cancel);
}
