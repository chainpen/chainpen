using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fillsign.App.ViewModels;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.App.Controls;

/// <summary>
/// FS-950: filling the PDF's own form fields on the page. A text field gets a text box over it while it's typed into;
/// a list or drop-down field offers its options in a menu beside it. Check boxes and radio buttons need nothing here:
/// the editor fills them on the click and the page is drawn again.
/// </summary>
public sealed partial class PagesCanvas
{
    private TextBox? _fieldBox;
    private FieldEdit? _fieldBoxEdit;

    private void AttachFields(EditorViewModel editor)
    {
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorViewModel.EditingField))
            {
                SyncFieldBox();
                InvalidateLayers();
            }
            else if (args.PropertyName is nameof(EditorViewModel.FocusedField) or nameof(EditorViewModel.FocusedOption))
            {
                InvalidateLayers();
            }
        };
        editor.FieldChoiceRequested += OfferChoices;
    }

    private void SyncFieldBox()
    {
        FieldEdit? editing = _editor?.EditingField;
        if (editing is null)
        {
            if (_fieldBox is { IsVisible: true })
            {
                _fieldBoxEdit = null;
                _fieldBox.IsVisible = false;
                _scroller?.Focus();
            }

            return;
        }

        if (_fieldBox is null)
        {
            _fieldBox = new TextBox
            {
                FontFamily = Rendering.ItemPainter.NotoSans,
                Padding = new Thickness(2, 0),
                Classes = { "formField" },
            };
            _fieldBox.KeyDown += (_, e) =>
            {
                // Enter finishes a one-line field; a multi-line field takes Enter as a new line.
                if (e.Key == Key.Enter && _fieldBoxEdit is { Hit.Field.IsMultiline: false })
                {
                    CommitFieldBox();
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab && _fieldBoxEdit is not null && _editor is not null)
                {
                    // Tab goes from field to field, as in PDF readers, instead of leaving the page.
                    _fieldBoxEdit = null;
                    _editor.CommitFieldEditAndMove(_fieldBox?.Text ?? string.Empty, backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    e.Handled = true;
                }
            };
            _fieldBox.LostFocus += (_, _) => CommitFieldBox();
            Children.Add(_fieldBox);
        }

        _fieldBoxEdit = editing;
        AutomationProperties.SetName(_fieldBox, $"Form field {editing.Hit.Field.Name}");
        _fieldBox.AcceptsReturn = editing.Hit.Field.IsMultiline;
        _fieldBox.TextWrapping = editing.Hit.Field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _fieldBox.VerticalContentAlignment = editing.Hit.Field.IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        _fieldBox.FontSize = FieldFontSize(editing.Hit) * Zoom;
        _fieldBox.Text = editing.Value;
        _fieldBox.IsVisible = true;
        if (editing.SelectsAll)
        {
            // Reached with Tab: the text is selected, so typing replaces it (as in a browser).
            _fieldBox.SelectionStart = 0;
            _fieldBox.SelectionEnd = editing.Value.Length;
        }
        else
        {
            _fieldBox.CaretIndex = editing.Value.Length;
        }
        InvalidateMeasure();
        InvalidateArrange();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _fieldBox?.Focus());
    }

    /// <summary>About the size PDF readers fill fields at: 11 pt, smaller for short boxes.</summary>
    private static double FieldFontSize(FieldHit hit) => Math.Min(11, Math.Max(6, hit.Box.Height * 0.7));

    /// <summary>Keeps what was typed in the field's text box.</summary>
    private void CommitFieldBox()
    {
        if (_fieldBox is null || _fieldBoxEdit is null || _editor is null)
        {
            return;
        }

        _fieldBoxEdit = null;
        _editor.CommitFieldEdit(_fieldBox.Text ?? string.Empty);
    }

    private void MeasureFieldBox() => _fieldBox?.Measure(Size.Infinity);

    private void ArrangeFieldBox()
    {
        if (_fieldBox is not { IsVisible: true } || _fieldBoxEdit is not { } editing || _editor is null)
        {
            return;
        }

        int index = _editor.State.IndexOf(editing.Hit.Page);
        if (index < 0 || index >= _tops.Length)
        {
            return;
        }

        Rect page = PageRect(index);
        Fillsign.Core.Model.PageRect box = editing.Hit.Box;
        _fieldBox.FontSize = FieldFontSize(editing.Hit) * Zoom;
        var rect = new Rect(page.X + (box.X * Zoom), page.Y + (box.Y * Zoom), box.Width * Zoom, box.Height * Zoom);
        _fieldBox.MinHeight = 0;
        _fieldBox.Arrange(rect);
    }

    /// <summary>A list or drop-down field: its options in a menu under it, the current one ticked.</summary>
    private void OfferChoices(FieldHit hit, string current)
    {
        int index = _editor?.State.IndexOf(hit.Page) ?? -1;
        if (_editor is null || index < 0)
        {
            return;
        }

        Rect page = PageRect(index);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        IReadOnlySet<string> chosen = hit.Field.IsMultiSelect ? PdfiumDocument.SplitChoices(current) : new HashSet<string> { current };
        foreach (string option in hit.Field.Options)
        {
            var item = new MenuItem
            {
                Header = option.Length > 0 ? option : "(empty)",
                Icon = chosen.Contains(option) ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += (_, _) => _editor?.ChooseOption(hit.Field, option);
            menu.Items.Add(item);
        }

        // An invisible anchor over the field, so the menu opens right under it.
        var anchor = new Border
        {
            Width = hit.Box.Width * Zoom,
            Height = hit.Box.Height * Zoom,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Children.Add(anchor);
        anchor.Measure(Size.Infinity);
        anchor.Arrange(new Rect(page.X + (hit.Box.X * Zoom), page.Y + (hit.Box.Y * Zoom), anchor.Width, anchor.Height));
        menu.Closed += (_, _) =>
        {
            Children.Remove(anchor);
            _scroller?.Focus();
        };

        // Opened from the keyboard or not, the arrow keys work in it straight away, starting on the current choice.
        menu.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int current = Math.Max(0, hit.Field.Options.ToList().FindIndex(chosen.Contains));
            (menu.Items.ElementAtOrDefault(current) as MenuItem)?.Focus(NavigationMethod.Directional);
        });
        menu.ShowAt(anchor);
    }

    /// <summary>The field under the pointer, for the hand cursor.</summary>
    private bool IsOverField(int pageIndex, Point position) =>
        _editor is not null && _editor.IsOverField(_editor.Pages[pageIndex].Id, ToPage(pageIndex, position));
}
