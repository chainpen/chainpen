using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fillsign.App.ViewModels;

namespace Fillsign.App.Views;

/// <summary>Export final copy (FS-608, 🎨 6): the name, the folder, the size, and whether to save the working copy too.</summary>
public partial class ExportDialog : UserControl
{
    private readonly TaskCompletionSource<ExportChoice?> _result = new();
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

    /// <summary>Where to export, or null to keep editing.</summary>
    public Task<ExportChoice?> Result => _result.Task;

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
