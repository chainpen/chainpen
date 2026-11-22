using Avalonia.Controls;
using Avalonia.Interactivity;
using Fillsign.App.Localization;
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
        UpdateExplanation();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Pages may have been added or removed since the card was made.
        UpdateExplanation();
    }

    private void UpdateExplanation()
    {
        if (DataContext is PageViewModel && _editor is not null)
        {
            Explanation.Text = ExplanationFor(_editor.Title, _editor.Pages.Count - 1);
        }
    }

    /// <summary>What the card says (D13), for any number of other pages.</summary>
    internal static string ExplanationFor(string title, int otherPages) =>
        Text.Format(
            otherPages switch
            {
                0 => Strings.DamagedPage_ExplanationOnlyPage,
                1 => Strings.DamagedPage_Explanation_One,
                _ => Strings.DamagedPage_Explanation_Other,
            },
            title,
            otherPages);

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
