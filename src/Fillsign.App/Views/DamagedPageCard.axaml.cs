using Avalonia.Controls;
using Avalonia.Interactivity;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

public partial class DamagedPageCard : UserControl
{
    private readonly EditorViewModel _editor;

    public DamagedPageCard()
        : this(null!)
    {
    }

    public DamagedPageCard(EditorViewModel editor)
    {
        _editor = editor;
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PageViewModel page && _editor is not null)
        {
            int others = _editor.Pages.Count - 1;
            Explanation.Text = $"This page is damaged in {_editor.Title}. "
                + (others == 1 ? "The other page is fine and you can keep working on it." : $"The other {others} pages are fine and you can keep working on them.");
        }
    }

    private void OnKeep(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PageViewModel page)
        {
            _editor.KeepDamagedPageCommand.Execute(page);
        }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PageViewModel page)
        {
            _editor.RemovePageCommand.Execute(page);
        }
    }
}
