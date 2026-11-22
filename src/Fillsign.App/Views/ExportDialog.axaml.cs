using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fillsign.App.Localization;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

/// <summary>Export final copy (FS-608, 🎨 6): the name, the folder, the size, and whether to save the working copy too.</summary>
public partial class ExportDialog : UserControl
{
    private readonly TaskCompletionSource<ExportDecision?> _result = new();
    private readonly ExportRequest _request;
    private string _folder;

    public ExportDialog()
        : this(new ExportRequest("document-final.pdf", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 0, false, true))
    {
    }

    public ExportDialog(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        _request = request;
        _folder = request.Folder;
        FileName.Text = request.SuggestedName;
        ShowFolder(ShortFolder(_folder));
        SizeText.Text = Text.Format(Strings.Export_FinalCopySize, SaveSizes.Describe(request.Size));
        AlsoSave.IsVisible = request.CanSaveWorkingCopy;
        LockFields.IsVisible = request.HasFormFields;
        if (request.HasFormFields)
        {
            // What the person sends depends on the lock: say which, so the opening line never contradicts it.
            LockFields.IsCheckedChanged += (_, _) => UpdateLede();
            UpdateLede();
        }
        AlsoSaveDetail.Text = request.ContainsSignature
            ? Strings.Export_AlsoSaveContainsSignature
            : Strings.Export_AlsoSaveSomewherePrivate;
        FileName.TextChanged += (_, _) => UpdateNotes();
        UpdateNotes();
        ShowGaps(request.Gaps, request.PageCount);
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                FileName.Focus();
                SelectNameWithoutExtension();
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowFromTheTop, Avalonia.Threading.DispatcherPriority.Background);
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _result.TrySetResult(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>Where to export, a gap to look at first, or null to keep editing.</summary>
    public Task<ExportDecision?> Result => _result.Task;

    // More than this and the list stops being read; the rest are a count, and the checking mode steps through them all.
    private const int MaxGapRows = 5;

    private void UpdateLede() => Lede.Text = LockFields.IsChecked == true
        ? Strings.Export_LedeLocked
        : Strings.Export_LedeEditable;

    /// <summary>
    /// Focusing the name box scrolls it into view, which nudged the dialog down a few points and cut off the top of the
    /// gaps list. Back to the top, as long as the name box is still in full view from there.
    /// </summary>
    private void ShowFromTheTop()
    {
        if (Body.Content is not Avalonia.Visual content || FileName.TranslatePoint(new Avalonia.Point(0, FileName.Bounds.Height), content) is not { } bottom)
        {
            return;
        }

        if (bottom.Y <= Body.Viewport.Height)
        {
            Body.Offset = default;
        }
    }

    /// <summary>
    /// "Possible gaps" (🎨 6): boxes are summed up per page ("3 of 4 boxes ticked on page 1") and each empty
    /// blank gets a row, each with Show me.
    /// </summary>
    private void ShowGaps(Core.Gaps.GapReport report, int pageCount)
    {
        if (report.Gaps.Count == 0)
        {
            return;
        }

        GapsSection.IsVisible = true;
        GapsNote.Text = report.PagesChecked < pageCount
            ? Strings.Export_GapsNoteSomePages
            : Strings.Export_GapsNote;

        var rows = new List<(string Title, string? Detail, int GapIndex)>();
        var boxPagesDone = new HashSet<Fillsign.Core.Model.PageId>();
        for (int i = 0; i < report.Gaps.Count; i++)
        {
            Core.Gaps.Gap gap = report.Gaps[i];
            if (gap.Kind != Core.Gaps.GapKind.Unticked)
            {
                rows.Add((EditorViewModel.DescribeForExport(gap), null, i));
                continue;
            }

            // A form's own question gets a row of its own, worded as a question; boxes read from the page text are
            // summed up per page instead.
            if (gap.IsField)
            {
                string options = gap.Question.Length > 0 && gap.Label.Length > 0 ? Text.Format(Strings.Export_OptionsFineToLeave, gap.Label) : Strings.Export_FineToLeave;
                rows.Add((EditorViewModel.DescribeForExport(gap), options, i));
                continue;
            }

            if (!boxPagesDone.Add(gap.Page))
            {
                continue;
            }

            Core.Gaps.PageBoxes? boxes = report.Boxes.FirstOrDefault(page => page.Page == gap.Page);
            string title = boxes is { } counted
                ? Text.Format(Strings.Export_BoxesTickedOnPage, counted.Ticked, counted.Total, gap.PageNumber)
                : EditorViewModel.DescribeForExport(gap);
            string detail = gap.Label.Length > 0 ? Text.Format(Strings.Export_NotTickedFineToLeave, gap.Label) : Strings.Export_FineToLeave;
            rows.Add((title, detail, i));
        }

        foreach ((string title, string? detail, int gapIndex) in rows.Take(MaxGapRows))
        {
            GapRows.Children.Add(GapRow(title, detail, gapIndex));
        }

        if (rows.Count > MaxGapRows)
        {
            GapRows.Children.Add(new Border
            {
                BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
                BorderBrush = Brush("BorderSubtle"),
                Padding = new Avalonia.Thickness(14, 10),
                Child = new TextBlock
                {
                    Classes = { "muted" },
                    FontSize = 13,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = Text.Format(Strings.Export_AndMoreShowMeStepsThrough, rows.Count - MaxGapRows),
                },
            });
        }
    }

    private Border GapRow(string title, string? detail, int gapIndex)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, Classes = { "muted" }, FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        var show = new Button { Content = Strings.Export_ShowMe, Classes = { "small" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(show, Text.Format(Strings.Export_ShowMeName, title));
        show.Click += (_, _) => _result.TrySetResult(new CheckGapChoice(gapIndex));

        var icon = new Fillsign.App.Controls.StrokeIcon { Size = 18, Foreground = Brush("Warning"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        if (Avalonia.Application.Current?.TryGetResource("IconWarning", Avalonia.Application.Current.ActualThemeVariant, out object? data) == true && data is Avalonia.Media.Geometry geometry)
        {
            icon.Data = geometry;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(show, 2);
        grid.Children.Add(show);
        return new Border
        {
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            BorderBrush = Brush("BorderSubtle"),
            Padding = new Avalonia.Thickness(14, 12),
            Child = grid,
        };
    }

    // The dialog isn't on screen yet while its rows are built, so the app's resources are looked up directly.
    private static Avalonia.Media.IBrush? Brush(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out object? value) == true ? value as Avalonia.Media.IBrush : null;

    /// <summary>
    /// Selects the name but not ".pdf", as the system's save dialogs do: typing replaces the name and the extension
    /// stays in view, so it doesn't look as if it will be lost.
    /// </summary>
    private void SelectNameWithoutExtension()
    {
        string name = FileName.Text ?? string.Empty;
        int end = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name.Length - ".pdf".Length : name.Length;
        FileName.SelectionStart = 0;
        FileName.SelectionEnd = end;
    }

    /// <summary>The full path the current name and folder make: ".pdf" is added when missing.</summary>
    public string? ChosenPath
    {
        get
        {
            string name = (FileName.Text ?? string.Empty).Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }

            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                name += ".pdf";
            }

            return Path.Combine(_folder, name);
        }
    }

    /// <summary>Picks the folder, as Change folder… would. For tests.</summary>
    internal void UseFolder(string folder)
    {
        _folder = folder;
        ShowFolder(ShortFolder(folder));
        UpdateNotes();
    }

    private void UpdateNotes()
    {
        // The document's own file is said straight away, in place of the note that exporting replaces a file: this
        // one it won't replace.
        bool isOwnFile = ChosenPath is { } own && _request.IsOwnFile(own);
        NameProblemText.Text = isOwnFile ? Strings.Export_ThatNameIsThisDocumentsOwnFile : Strings.Export_TypeANameForTheFinal;
        NameProblem.IsVisible = isOwnFile;
        ReplaceNote.IsVisible = !isOwnFile && ChosenPath is { } path && File.Exists(path);
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        if (ChosenPath is not { } path || _request.IsOwnFile(path))
        {
            // Keep the button working and say what's needed (INTERACTION-PATTERNS §6).
            NameProblem.IsVisible = true;
            FileName.Focus();
            return;
        }

        _result.TrySetResult(new ExportChoice(path, AlsoSave.IsVisible && AlsoSave.IsChecked == true, LockFields.IsVisible && LockFields.IsChecked == true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _result.TrySetResult(null);

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnExport(sender, e);
            e.Handled = true;
        }
    }

    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IStorageFolder? start = await storage.TryGetFolderFromPathAsync(_folder).ConfigureAwait(true);
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Export_ChooseWhereToSaveTheFinal,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } chosen)
        {
            _folder = chosen;
            ShowFolder(ShortFolder(chosen));
            UpdateNotes();
        }
    }

    private static string ShortFolder(string folder) => SaveSizes.ShortFolder(folder);

    /// <summary>"Saves to Documents": the folder in bold, wherever the language puts it in the sentence.</summary>
    private void ShowFolder(string folder)
    {
        string[] around = Strings.Export_SavesToFolder.Split("{0}");
        var name = new Avalonia.Controls.Documents.Run(folder) { FontWeight = Avalonia.Media.FontWeight.SemiBold };
        name.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, this.GetResourceObservable("Text"));
        SavesTo.Inlines = [new Avalonia.Controls.Documents.Run(around[0]), name, new Avalonia.Controls.Documents.Run(around.Length > 1 ? around[1] : string.Empty)];
    }
}
