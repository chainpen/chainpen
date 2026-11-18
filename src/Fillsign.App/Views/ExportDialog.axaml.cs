using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

/// <summary>Export final copy (FS-608, 🎨 6): the name, the folder, the size, and whether to save the working copy too.</summary>
public partial class ExportDialog : UserControl
{
    private readonly TaskCompletionSource<ExportDecision?> _result = new();
    private string _folder;

    public ExportDialog()
        : this(new ExportRequest("document-final.pdf", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 0, false, true))
    {
    }

    public ExportDialog(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        _folder = request.Folder;
        FileName.Text = request.SuggestedName;
        FolderText.Text = ShortFolder(_folder);
        SizeText.Text = $"Final copy {SaveSizes.Describe(request.Size)}";
        AlsoSave.IsVisible = request.CanSaveWorkingCopy;
        AlsoSaveDetail.Text = request.ContainsSignature
            ? "Keeps your edits changeable. It contains your signature, so keep it private and send only the final copy."
            : "Keeps your edits changeable. Save it somewhere private, and send only the final copy.";
        FileName.TextChanged += (_, _) => UpdateNotes();
        UpdateNotes();
        ShowGaps(request.Gaps, request.PageCount);
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                FileName.Focus();
                FileName.SelectAll();
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

    /// <summary>
    /// "Possible gaps we spotted" (🎨 6): boxes are summed up per page ("3 of 4 boxes ticked on page 1") and each empty
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
            ? "We can’t check everything, and we only looked at pages you’ve added to. Look over each page before you send it."
            : "We can’t check everything. Look over each page before you send it.";

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

            if (!boxPagesDone.Add(gap.Page))
            {
                continue;
            }

            Core.Gaps.PageBoxes? boxes = report.Boxes.FirstOrDefault(page => page.Page == gap.Page);
            string title = boxes is { } counted
                ? $"{counted.Ticked} of {counted.Total} boxes ticked on page {gap.PageNumber}"
                : EditorViewModel.DescribeForExport(gap);
            string? detail = gap.Label.Length > 0 ? $"Not ticked: “{gap.Label}”. Fine to leave if that’s intended." : "Fine to leave if that’s intended.";
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
                    Text = $"And {rows.Count - MaxGapRows} more. Show me steps through them all.",
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

        var show = new Button { Content = "Show me", Classes = { "small" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(show, $"Show me: {title}");
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
        FolderText.Text = ShortFolder(folder);
        UpdateNotes();
    }

    private void UpdateNotes()
    {
        NameProblem.IsVisible = false;
        ReplaceNote.IsVisible = ChosenPath is { } path && File.Exists(path);
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        if (ChosenPath is not { } path)
        {
            // Keep the button working and say what's needed (INTERACTION-PATTERNS §6).
            NameProblem.IsVisible = true;
            FileName.Focus();
            return;
        }

        _result.TrySetResult(new ExportChoice(path, AlsoSave.IsVisible && AlsoSave.IsChecked == true));
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
            Title = "Choose where to save the final copy",
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } chosen)
        {
            _folder = chosen;
            FolderText.Text = ShortFolder(chosen);
            UpdateNotes();
        }
    }

    private static string ShortFolder(string folder) => SaveSizes.ShortFolder(folder);
}
