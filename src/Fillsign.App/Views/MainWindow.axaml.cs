using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Fillsign.App.Platform;
using Fillsign.App.Platform.Signatures;
using Fillsign.App.ViewModels;
using Fillsign.Core.Model;
using Fillsign.Pdf.Import;

namespace Fillsign.App.Views;

/// <summary>
/// One document window. Shows the start screen or the editor, runs the open flow (FR-1), and hosts the dialogs.
/// Opening while a document is open never replaces it (FR-1.5).
/// </summary>
public partial class MainWindow : Window, IEditorHost
{
    private const string PracticeFormName = "Practice form.pdf";
    private const double DefaultDialogTop = 160;
    private static readonly FilePickerFileType s_documents = new("PDFs and images")
    {
        Patterns = ["*.pdf", "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp", "*.heic", "*.heif"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf", "public.image"],
        MimeTypes = ["application/pdf", "image/*"],
    };

    private static readonly FilePickerFileType s_photos = new("Photos and scans")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp", "*.heic", "*.heif"],
        AppleUniformTypeIdentifiers = ["public.image"],
        MimeTypes = ["image/*"],
    };

    // Every window shares one store, so a signature kept in one window shows in the others.
    private static readonly Lazy<SavedSignatureStore> s_defaultSignatureStore = new(SavedSignatureStore.ForCurrentUser);

    private readonly MainWindowViewModel _viewModel;
    // Dialogs show one at a time: each waits for the one before it.
    private Task _previousDialog = Task.CompletedTask;
    private StartView? _startView;
    private EditorView? _editorView;

    public MainWindow()
        : this(isFirstLaunch: false, [])
    {
    }

    public MainWindow(bool isFirstLaunch, IReadOnlyList<string> pathsToOpen)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(isFirstLaunch);
        DataContext = _viewModel;
        ShowStart();
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        KeyDown += OnKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Editor?.Dispose();
        if (pathsToOpen.Count > 0)
        {
            Opened += async (_, _) => await OpenPathsAsync(pathsToOpen).ConfigureAwait(true);
        }
    }

    // ----- Opening -----

    /// <summary>Opens files here if nothing is open; otherwise asks whether to add them or use a new window (D5).</summary>
    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (_viewModel.Editor is { } editor)
        {
            string title = paths.Count == 1 ? $"Open {Path.GetFileName(paths[0])}" : $"Open {paths.Count} files";
            OpenChoice choice = await AskOpenOrAddAsync(title, editor.Title, 0).ConfigureAwait(true);
            if (choice == OpenChoice.NewWindow)
            {
                OpenInNewWindow(paths);
            }
            else if (choice == OpenChoice.AddPages)
            {
                await editor.AddFilesAsync(paths).ConfigureAwait(true);
            }

            return;
        }

        var files = await Task.WhenAll(paths.Select(path => InputFile.ReadAsync(path, CancellationToken.None))).ConfigureAwait(true);
        await OpenFilesAsync(files).ConfigureAwait(true);
    }

    private async Task OpenFilesAsync(InputFile[] files, bool isPracticeForm = false, bool restoreRecord = true)
    {
        StartViewModel start = _viewModel.Start;
        start.IsOpening = true;
        start.Problem = null;
        try
        {
            OpenResult result = await DocumentLoader.OpenAsync(files, AskPasswordAsync, CancellationToken.None, restoreRecord).ConfigureAwait(true);
            var editor = new EditorViewModel(result.Source, this);
            editor.ApplyRestore(result.Restore);
            ReplaceEditor(editor);
            if (!result.Skipped.IsEmpty)
            {
                await ShowProblemsAsync(result.Skipped, files.Length - result.Skipped.Length).ConfigureAwait(true);
            }

            if (isPracticeForm)
            {
                ShowFirstRunTipsOnce();
            }
        }
        catch (DocumentOpenException ex)
        {
            if (ex.Problems.Count == 1)
            {
                start.Problem = ex.Problems[0];
            }
            else
            {
                await ShowProblemsAsync(ex.Problems, 0).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Every file was skipped at the password prompt: nothing to do.
        }
        finally
        {
            start.IsOpening = false;
        }
    }

    private async Task ChooseAndOpenAsync()
    {
        IReadOnlyList<string> paths = await ChooseFilesAsync().ConfigureAwait(true);
        await OpenPathsAsync(paths).ConfigureAwait(true);
    }

    private async Task OpenPracticeFormAsync()
    {
        await using Stream stream = AssetLoader.Open(new Uri("avares://Fillsign.App/Assets/practice-form.pdf"));
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy).ConfigureAwait(true);
        await OpenFilesAsync([new InputFile(PracticeFormName, null, copy.ToArray())], isPracticeForm: true).ConfigureAwait(true);
    }

    private void ShowStart()
    {
        _startView = new StartView { DataContext = _viewModel.Start };
        _startView.OpenRequested += async (_, _) => await ChooseAndOpenAsync().ConfigureAwait(true);
        _startView.PracticeFormRequested += async (_, _) => await OpenPracticeFormAsync().ConfigureAwait(true);
        Host.Content = _startView;
    }

    /// <summary>Shows a newly opened document, closing the one it replaces ("Open final copy instead", restoring edits).</summary>
    private void ReplaceEditor(EditorViewModel editor)
    {
        EditorViewModel? previous = _viewModel.Editor;
        _viewModel.ShowEditor(editor);
        ShowEditor(editor);
        previous?.Dispose();
        _ = editor.LoadAsync();
    }

    // ----- Closing (D4, FR-8.11) -----

    private bool _isCloseConfirmed;

    /// <summary>Closes without asking about unsaved changes. Tests use this to tidy up.</summary>
    internal void CloseWithoutAsking()
    {
        _isCloseConfirmed = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isCloseConfirmed || _viewModel.Editor is not { HasUnsavedChanges: true } editor)
        {
            return;
        }

        e.Cancel = true;
        _ = AskBeforeClosingAsync(editor);
    }

    /// <summary>D4: Save is the default and has focus; Don't save is never the default (FR-8.11).</summary>
    private async Task AskBeforeClosingAsync(EditorViewModel editor)
    {
        string since = editor.LastSavedAt is { } saved ? $" since {saved.ToLocalTime():HH:mm}" : string.Empty;
        Choice choice = await ShowDialogAsync(
            new ChoiceDialog(
                $"Save changes to {editor.Title} before closing?",
                $"Your changes{since} aren’t saved to the file. If you don’t save, they’re lost.",
                "Save",
                "Don’t save",
                focusMain: true),
            dialog => dialog.Result).ConfigureAwait(true);
        bool canClose = choice switch
        {
            Choice.Main => await editor.SaveAsync().ConfigureAwait(true),
            Choice.Other => true,
            _ => false,
        };
        if (canClose)
        {
            CloseWithoutAsking();
        }
    }

    private void ShowEditor(EditorViewModel editor)
    {
        _editorView = new EditorView { DataContext = editor };
        _editorView.OpenRequested += async (_, _) => await ChooseAndOpenAsync().ConfigureAwait(true);
        _editorView.AddPagesRequested += async (_, _) =>
        {
            IReadOnlyList<string> paths = await ChooseFilesAsync().ConfigureAwait(true);
            if (paths.Count > 0)
            {
                await editor.AddFilesAsync(paths).ConfigureAwait(true);
            }
        };
        _editorView.AddFilesRequested += async (paths, at) => await editor.AddFilesAsync(paths, at).ConfigureAwait(true);
        _editorView.NewWindowRequested += OpenInNewWindow;
        Host.Content = _editorView;
        _startView = null;
    }

    private void ShowFirstRunTipsOnce()
    {
        AppSettings settings = AppSettings.Load();
        if (settings.HasSeenFirstRunTips)
        {
            return;
        }

        (settings with { HasSeenFirstRunTips = true }).Save();
        FirstRunSteps.ItemsSource = new[]
        {
            "1   Tick the boxes that apply",
            "2   Text or Date on the lines",
            "3   Sign on the signature line",
            "4   Export final copy to get a file to send",
        };
        FirstRunTips.IsVisible = true;
    }

    private void OnDismissTips(object? sender, RoutedEventArgs e) => FirstRunTips.IsVisible = false;

    // ----- Dropping on the start screen -----

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel.Editor is null)
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        }
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        // The editor handles its own drops (two targets, E9).
        if (_viewModel.Editor is not null || e.Handled)
        {
            return;
        }

        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>()];
        await OpenPathsAsync(paths).ConfigureAwait(true);
    }

    // ----- Keyboard (FR-7, platform command key) -----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DialogLayer.IsVisible || e.Handled)
        {
            return;
        }

        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool hasCommand = e.KeyModifiers.HasFlag(command);
        bool hasShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        EditorViewModel? editor = _viewModel.Editor;

        // Typing in a text box (e.g. Go to page) keeps its own keys (FR-7.12).
        bool isTyping = FocusManager?.GetFocusedElement() is TextBox;

        if (editor is { IsOrganising: true } && !isTyping && HandleOrganiseKey(editor, e.Key, hasCommand))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.O when hasCommand:
                _ = ChooseAndOpenAsync();
                break;
            case Key.S when hasCommand && hasShift && editor is not null:
                _ = editor.SaveAsCommand.ExecuteAsync(null);
                break;
            case Key.S when hasCommand && editor is not null:
                _ = editor.SaveCommand.ExecuteAsync(null);
                break;
            case Key.E when hasCommand && editor is not null:
                _ = editor.ExportCommand.ExecuteAsync(null);
                break;
            case Key.OemPlus or Key.Add when hasCommand && editor is not null:
                _editorView?.ZoomIn();
                break;
            case Key.OemMinus or Key.Subtract when hasCommand && editor is not null:
                _editorView?.ZoomOut();
                break;
            case Key.D0 or Key.NumPad0 when hasCommand && editor is not null:
                _editorView?.FitWidth();
                break;
            case Key.Z when hasCommand && hasShift && editor is not null && !isTyping:
                editor.RedoCommand.Execute(null);
                break;
            case Key.Z when hasCommand && editor is not null && !isTyping:
                editor.UndoCommand.Execute(null);
                break;
            case Key.PageDown when editor is not null && !isTyping:
                editor.NextPageCommand.Execute(null);
                break;
            case Key.PageUp when editor is not null && !isTyping:
                editor.PreviousPageCommand.Execute(null);
                break;
            case Key.Escape when FirstRunTips.IsVisible:
                FirstRunTips.IsVisible = false;
                break;
            case Key.Escape when editor is not null:
                // Back out one level, including finishing typing (FR-7.7).
                if (!editor.BackOut())
                {
                    return;
                }

                break;
            case Key.Delete or Key.Back when editor is not null && !isTyping && editor.DeleteSelectionCommand.CanExecute(null):
                editor.DeleteSelectionCommand.Execute(null);
                break;
            case Key.A when hasCommand && editor is not null && !isTyping:
                editor.SelectAllOnCurrentPage();
                break;
            case Key.C when hasCommand && editor is not null && !isTyping:
                editor.Copy();
                break;
            case Key.X when hasCommand && editor is not null && !isTyping:
                editor.Cut();
                break;
            case Key.V when hasCommand && editor is not null && !isTyping:
                editor.Paste();
                break;
            case Key.D when hasCommand && editor is not null && !isTyping && editor.DuplicateSelectionCommand.CanExecute(null):
                editor.DuplicateSelectionCommand.Execute(null);
                break;

            // The page is one Tab stop; arrow keys and Enter work on it (INTERACTION-PATTERNS §11, NFR-4).
            case Key.Left when IsOnPage(editor, isTyping) && editor!.Arrow(-1, 0, hasShift):
            case Key.Right when IsOnPage(editor, isTyping) && editor!.Arrow(1, 0, hasShift):
            case Key.Up when IsOnPage(editor, isTyping) && editor!.Arrow(0, -1, hasShift):
            case Key.Down when IsOnPage(editor, isTyping) && editor!.Arrow(0, 1, hasShift):
            case Key.Enter when IsOnPage(editor, isTyping) && editor!.Enter():
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Organise pages (FR-6, FR-7): the item shortcuts act on the selected pages instead.</summary>
    private static bool HandleOrganiseKey(EditorViewModel editor, Key key, bool hasCommand)
    {
        switch (key)
        {
            case Key.Delete or Key.Back when editor.DeletePagesCommand.CanExecute(null):
                editor.DeletePagesCommand.Execute(null);
                return true;
            case Key.A when hasCommand:
                editor.SelectAllPagesCommand.Execute(null);
                return true;
            case Key.D when hasCommand && editor.DuplicatePagesCommand.CanExecute(null):
                editor.DuplicatePagesCommand.Execute(null);
                return true;
            case Key.Escape when editor.HasPageSelection:
                editor.ClearPageSelectionCommand.Execute(null);
                return true;
            case Key.Escape:
                editor.ShowEditContentCommand.Execute(null);
                return true;
            case Key.C or Key.X or Key.V when hasCommand:
                // Copying pages between documents isn't offered yet; don't let these reach the items.
                return true;
            default:
                return false;
        }
    }

    private bool IsOnPage(EditorViewModel? editor, bool isTyping) =>
        editor is not null && !isTyping && _editorView is not null && _editorView.IsPageFocused(FocusManager?.GetFocusedElement());

    // ----- IEditorHost -----

    public async Task<IReadOnlyList<string>> ChooseFilesAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF or image",
            AllowMultiple = true,
            FileTypeFilter = [s_documents, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    public Task<string?> AskPasswordAsync(string fileName, bool wasWrong) =>
        ShowDialogAsync(new PasswordDialog(fileName, wasWrong), dialog => dialog.Result);

    public Task<OpenChoice> AskOpenOrAddAsync(string title, string documentName, int pageCount) =>
        ShowDialogAsync(new OpenOrAddDialog(title, documentName, pageCount), dialog => dialog.Result);

    public async Task ShowProblemsAsync(IReadOnlyList<OpenProblem> problems, int openedCount)
    {
        if (problems.Count == 0)
        {
            return;
        }

        int total = problems.Count + openedCount;
        string title = openedCount > 0
            ? $"{openedCount} of {total} files were opened"
            : problems.Count == 1 ? problems[0].Title : "These files can’t be opened";
        var paragraphs = problems.Select(problem => problems.Count == 1 && openedCount == 0 ? problem.Message : $"{problem.Title}. {problem.Message}").ToList();
        await ShowDialogAsync(new MessageDialog(title, paragraphs, "Close"), dialog => dialog.Closed.ContinueWith(_ => true, TaskScheduler.Default)).ConfigureAwait(true);
    }

    public async Task ShowMessageAsync(string title, string message) =>
        await ShowDialogAsync(new MessageDialog(title, [message], "Got it"), dialog => dialog.Closed.ContinueWith(_ => true, TaskScheduler.Default)).ConfigureAwait(true);

    /// <summary>Saved signatures and initials. Tests point this at a temporary folder.</summary>
    internal static SavedSignatureStore? SignatureStoreOverride { get; set; }

    private static SavedSignatureStore SignatureStore => SignatureStoreOverride ?? s_defaultSignatureStore.Value;

    /// <summary>The signature dialog (FR-3.4, FR-4): choose a saved one or make one, then place it.</summary>
    public async Task<(SignatureImage Image, SignatureKind Kind)?> ChooseSignatureAsync()
    {
        SavedSignatureStore store = SignatureStore;

        // Reading saved signatures can ask for Keychain or keyring access, so keep it off the UI thread.
        await Task.Run(store.Load).ConfigureAwait(true);
        using var viewModel = new SignatureDialogViewModel(store, ChoosePhotoFileAsync, TimeProvider.System);
        return await ShowDialogAsync(new SignatureDialog(viewModel), _ => viewModel.Result, top: 40).ConfigureAwait(true);
    }

    private async Task<string?> ChoosePhotoFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a photo or scan of your signature",
            AllowMultiple = false,
            FileTypeFilter = [s_photos, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    /// <summary>Stands in for the system's save dialog in tests: (suggested name, folder) → chosen path or null.</summary>
    internal static Func<string, string, string?>? SavePathOverride { get; set; }

    public async Task<string?> AskWorkingCopyPathAsync(string suggestedName, string folder)
    {
        if (SavePathOverride is { } chooseForTest)
        {
            return chooseForTest(suggestedName, folder);
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save your working copy",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder).ConfigureAwait(true),
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"], AppleUniformTypeIdentifiers = ["com.adobe.pdf"], MimeTypes = ["application/pdf"] }],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public Task<ExportChoice?> AskExportAsync(ExportRequest request) =>
        ShowDialogAsync(new ExportDialog(request), dialog => dialog.Result, top: 64);

    public async Task<bool> ConfirmRestoreAsync(DateTimeOffset? savedAt)
    {
        string when = savedAt is { } saved ? $" from {saved.ToLocalTime():dd/MM/yyyy, HH:mm}" : string.Empty;
        Choice choice = await ShowDialogAsync(
            new ChoiceDialog(
                "Restore your Fillsign edits?",
                $"Your editable ticks, text and signature{when} come back, and the changes made in the other app are removed from this window. The file isn’t changed until you save.",
                "Restore edits",
                otherLabel: null,
                focusMain: false,
                isMainDestructive: true),
            dialog => dialog.Result).ConfigureAwait(true);
        return choice == Choice.Main;
    }

    public void ShowInFolder(string path)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo start = OperatingSystem.IsMacOS() ? new("open") { ArgumentList = { "-R", path } }
                : OperatingSystem.IsWindows() ? new("explorer.exe") { ArgumentList = { $"/select,{path}" } }
                : new("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path } };
            start.UseShellExecute = false;
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"The folder could not be shown. {ex.Message}");
        }
    }

    public async Task OpenAsFinalCopyAsync(string path)
    {
        InputFile file = await InputFile.ReadAsync(path, CancellationToken.None).ConfigureAwait(true);
        await OpenFilesAsync([file], restoreRecord: false).ConfigureAwait(true);
    }

    public Task RestoreEditsAsync(WorkingCopyRestore restore)
    {
        if (_viewModel.Editor is not { } current)
        {
            return Task.CompletedTask;
        }

        SourceDocument clean = DocumentLoader.OpenCleanSource(current.Source.Name, current.Source.Path, restore.CleanSource);
        var editor = new EditorViewModel(clean, this);
        editor.RestoreEditsOver(restore);
        ReplaceEditor(editor);
        return Task.CompletedTask;
    }

    public Task<int?> AskMovePositionAsync(int count, int pageCount) =>
        ShowDialogAsync(new MoveToDialog(count, pageCount), dialog => dialog.Result);

    public void OpenInNewWindow(IReadOnlyList<string> paths)
    {
        var window = new MainWindow(isFirstLaunch: false, paths);
        window.Show();
    }

    /// <summary>Shows one dialog at a time over the dimmed window and returns its answer.</summary>
    /// <param name="top">Distance from the top of the window; tall dialogs sit higher so they fit.</param>
    private async Task<TResult> ShowDialogAsync<TDialog, TResult>(TDialog dialog, Func<TDialog, Task<TResult>> result, double top = DefaultDialogTop)
        where TDialog : Control
    {
        Task before = _previousDialog;
        var done = new TaskCompletionSource();
        _previousDialog = done.Task;
        await before.ConfigureAwait(true);
        IInputElement? previousFocus = FocusManager?.GetFocusedElement();
        try
        {
            DialogHost.Margin = new Thickness(0, top, 0, 0);
            DialogHost.Content = dialog;
            DialogLayer.IsVisible = true;
            return await result(dialog).ConfigureAwait(true);
        }
        finally
        {
            DialogLayer.IsVisible = false;
            DialogHost.Content = null;
            previousFocus?.Focus();
            done.SetResult();
        }
    }
}
