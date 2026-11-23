using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Chainpen.App.Localization;
using Chainpen.App.ViewModels;
using Chainpen.Pdf.Pdfium;

namespace Chainpen.App.Controls;

/// <summary>
/// FS-950: filling the PDF's own form fields on the page. A text field gets a text box over it while it's typed into;
/// a list or drop-down field offers its options in a menu beside it. Check boxes and radio buttons need nothing here:
/// the editor fills them on the click and the page is drawn again.
/// </summary>
public sealed partial class PagesCanvas
{

    private TextBox? _fieldBox;
    private FieldEdit? _fieldBoxEdit;
    private CombCells? _combCells;

    private void AttachFields(EditorViewModel editor)
    {
        editor.PropertyChanged += OnFieldsPropertyChanged;
        editor.FieldChoiceRequested += OfferChoices;
    }

    private void DetachFields(EditorViewModel editor)
    {
        editor.PropertyChanged -= OnFieldsPropertyChanged;
        editor.FieldChoiceRequested -= OfferChoices;
    }

    private void OnFieldsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
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
        else if (args.PropertyName == nameof(EditorViewModel.CurrentPageIndex))
        {
            // Screen readers see the fields near the page in view; moving on changes which those are.
            (ControlAutomationPeer.FromElement(this) as PagesCanvasAutomationPeer)?.RefreshFields();
        }
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
                _combCells?.IsVisible = false;
                _scroller?.Focus();
            }

            return;
        }

        if (_fieldBox is null)
        {
            _fieldBox = CreateFieldBox();
        }
        else if (!ReferenceEquals(_fieldBoxEdit, editing))
        {
            // Each field gets the other of two boxes, so focus lands on a different element: from one text field to
            // the next (Tab), macOS saw the same box regain focus and VoiceOver said nothing (UX review).
            TextBox previous = _fieldBox;
            _fieldBox = _spareFieldBox ?? CreateFieldBox();
            _spareFieldBox = previous;
            previous.IsVisible = false;
        }

        _fieldBoxEdit = editing;
        AutomationProperties.SetName(_fieldBox, EditorViewModel.FieldBoxName(editing.Hit.Field));
        _fieldBox.AcceptsReturn = editing.Hit.Field.IsMultiline;
        _fieldBox.MaxLength = editing.Hit.Field.MaxLength; // 0 is no limit (FS-963)
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

    private TextBox? _spareFieldBox;

    /// <summary>A text box for typing into a form field in place; two take turns, so Tab moves focus between them.</summary>
    private TextBox CreateFieldBox()
    {
        var box = new TextBox
        {
            FontFamily = Rendering.ItemPainter.NotoSans,
            Padding = new Thickness(2, 0),
            Classes = { "formField" },
        };
        box.KeyDown += (_, e) =>
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
        box.TextChanged += (_, _) =>
        {
            // The editor keeps up with the box, so a keyboard Save or Export while it has focus includes its text.
            if (ReferenceEquals(box, _fieldBox) && _fieldBoxEdit is not null)
            {
                _editor?.UpdateFieldEdit(box.Text ?? string.Empty);
            }
        };
        box.LostFocus += (_, _) =>
        {
            // Only the box in use: the other one losing focus has already been committed.
            if (ReferenceEquals(box, _fieldBox))
            {
                CommitFieldBox();
            }
        };
        Children.Add(box);
        return box;
    }

    /// <summary>
    /// The size the field's text will be drawn at, so it doesn't jump when typing finishes: the field's own size, or for
    /// fields sized to fit, about what PDF readers fit to the box (12 pt for several lines).
    /// </summary>
    private static double FieldFontSize(FieldHit hit) => hit.Field.FontSize > 0
        ? hit.Field.FontSize
        : hit.Field.IsMultiline ? 12 : Math.Clamp((hit.Box.Height - 4) * 0.75, 6, 12);

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
        Chainpen.Core.Model.PageRect box = editing.Hit.Box;
        _fieldBox.FontSize = FieldFontSize(editing.Hit) * Zoom;
        var rect = new Rect(page.X + (box.X * Zoom), page.Y + (box.Y * Zoom), box.Width * Zoom, box.Height * Zoom);
        _fieldBox.MinHeight = 0;
        _fieldBox.Arrange(rect);
        ArrangeComb(editing.Hit.Field, rect);
    }

    /// <summary>
    /// FS-963: a field with a box per character is typed into as it'll look: its boxes are drawn over the text box and
    /// each character is spaced to sit in the middle of its own box.
    /// </summary>
    private void ArrangeComb(FormField field, Rect rect)
    {
        if (_fieldBox is null)
        {
            return;
        }

        TextPresenter? presenter = _fieldBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (!field.IsComb)
        {
            _combCells?.IsVisible = false;
            presenter?.LetterSpacing = 0;
            _fieldBox.Padding = new Thickness(2, 0);
            return;
        }

        if (_combCells is null)
        {
            _combCells = new CombCells { IsHitTestVisible = false };
            Children.Add(_combCells);
        }

        double cell = rect.Width / field.MaxLength;
        double glyph = new FormattedText("0", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Rendering.ItemPainter.NotoSans), _fieldBox.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;
        _fieldBox.Padding = new Thickness(Math.Max(0, (cell - glyph) / 2), 0, 0, 0);
        presenter?.LetterSpacing = Math.Max(0, cell - glyph);
        _combCells.Cells = field.MaxLength;
        _combCells.IsVisible = true;
        _combCells.Measure(rect.Size);
        _combCells.Arrange(rect);
        _combCells.InvalidateVisual();
    }

    /// <summary>The dividers between a comb field's boxes, drawn over its text box while it's typed into.</summary>
    private sealed class CombCells : Control
    {
        public int Cells { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Cells < 2)
            {
                return;
            }

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), 1);
            double cell = Bounds.Width / Cells;
            for (int i = 1; i < Cells; i++)
            {
                double x = Math.Round(i * cell) + 0.5;
                context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
            }
        }
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
                Header = option.Length > 0 ? option : Strings.Editor_EmptyOption,
                Icon = chosen.Contains(option) ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += (_, _) => _editor?.ChooseOption(hit.Field, option);
            menu.Items.Add(item);
        }

        // A drop-down that takes typing (FS-964): an answer that isn't in the list.
        if (hit.Field.IsEditable)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            var type = new MenuItem { Header = Strings.Editor_TypeAnAnswer };
            type.Click += (_, _) => _editor?.TypeIntoField(hit);
            menu.Items.Add(type);
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

    /// <summary>The editor this page shows, for its screen-reader view.</summary>
    internal EditorViewModel? Editor => _editor;

    protected override AutomationPeer OnCreateAutomationPeer() => new PagesCanvasAutomationPeer(this);

    /// <summary>Where a field is, relative to the window, for screen readers' highlight. Null when its page isn't laid out.</summary>
    internal Rect? FieldBoundsInWindow(FieldHit hit)
    {
        int index = _editor?.State.IndexOf(hit.Page) ?? -1;
        if (index < 0 || index >= _tops.Length || TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        Rect page = PageRect(index);
        var local = new Rect(page.X + (hit.Box.X * Zoom), page.Y + (hit.Box.Y * Zoom), hit.Box.Width * Zoom, hit.Box.Height * Zoom);
        return this.TranslatePoint(local.TopLeft, top) is { } origin ? new Rect(origin, local.Size) : null;
    }

    /// <summary>The field under the pointer, for the hand cursor.</summary>
    private bool IsOverField(int pageIndex, Point position) =>
        _editor is not null && _editor.IsOverField(_editor.Pages[pageIndex].Id, ToPage(pageIndex, position));
}
