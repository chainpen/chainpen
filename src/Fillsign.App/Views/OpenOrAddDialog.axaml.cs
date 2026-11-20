using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class OpenOrAddDialog : UserControl
{
    private readonly TaskCompletionSource<OpenChoice> _result = new();

    public OpenOrAddDialog()
        : this("Open", "this document", 0)
    {
    }

    public OpenOrAddDialog(string title, string documentName, int pageCount)
    {
        InitializeComponent();
        Heading.Text = title;
        Body.Text = $"{documentName} stays open either way.";
        AddDetail.Text = pageCount switch
        {
            1 => "1 page goes at the end. You can move it afterwards.",
            > 1 => $"{pageCount} pages go at the end. You can move them afterwards.",
            _ => "The pages go at the end. You can move them afterwards.",
        };
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
