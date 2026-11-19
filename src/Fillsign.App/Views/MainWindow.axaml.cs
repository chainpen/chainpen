using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    // Writes the recovery copy while there are unsaved changes (FS-704).
    private readonly Avalonia.Threading.DispatcherTimer _autosave = new() { Interval = EditorViewModel.AutosaveInterval };
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
        TitleBar.MergeInto(this);
        RestorePlacement();
        Closed += (_, _) => RememberPlacement();
        Opened += (_, _) => WindowFit.KeepOnScreen(this);
        _viewModel = new MainWindowViewModel(isFirstLaunch);
        DataContext = _viewModel;
        BuildMenu();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Editor))
            {
                UpdateMenuForDocument();
            }
        };
        ShowStart();
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        KeyDown += OnKeyDown;

        // Tab on the page steps through items (FS-716); it's caught on the way down, before focus moves away.
        AddHandler(KeyDownEvent, OnTabOnPage, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnFieldKeys, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SizeChanged += (_, _) =>
        {
            if (DialogLayer.IsVisible)
            {
                FitDialogToWindow();
            }
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _autosave.Stop();
            if (_viewModel.Editor is { } editor)
            {
                // Closing normally means the work was saved or deliberately discarded: nothing to recover. Before an
                // update the copy is kept, to be offered back after the restart.
                if (!_isKeptForUpdate)
                {
                    editor.DiscardRecoveryCopy();
                }

                editor.Dispose();
            }
        };
        _autosave.Tick += async (_, _) =>
        {
            if (_viewModel.Editor is { } editor)
            {
                await editor.AutosaveAsync().ConfigureAwait(true);
            }
        };
        _autosave.Start();
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

    /// <summary>Opens files in this window and returns the editor, or null when nothing opened.</summary>
    private async Task<EditorViewModel?> OpenFilesAsync(InputFile[] files, bool isPracticeForm = false, bool restoreRecord = true)
    {
        StartViewModel start = _viewModel.Start;
        start.IsOpening = true;
        start.Problem = null;
        try
        {
            OpenResult result = await DocumentLoader.OpenAsync(files, AskPasswordAsync, CancellationToken.None, restoreRecord).ConfigureAwait(true);
            RestrictedChoice restricted = result.RestrictedFiles.IsEmpty ? RestrictedChoice.EditAnyway : await AskAboutRestrictedAsync(result.RestrictedFiles).ConfigureAwait(true);
            if (restricted == RestrictedChoice.DontOpen)
            {
                result.Source.Dispose();
                return null;
            }

            var editor = new EditorViewModel(result.Source, this) { IsViewOnly = restricted == RestrictedChoice.ViewOnly };
            editor.ApplyRestore(result.Restore);
            ReplaceEditor(editor);
            if (files is [{ Path: { } path }])
            {
                AppSettings.Update(settings => settings.WithRecent(path, DateTimeOffset.Now));
            }
            if (!result.Skipped.IsEmpty)
            {
                await ShowProblemsAsync(result.Skipped, files.Length - result.Skipped.Length).ConfigureAwait(true);
            }

            if (isPracticeForm)
            {
                ShowFirstRunTipsOnce();
            }

            return editor;
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

        return null;
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
        _viewModel.Start.Refresh();
        _startView = new StartView { DataContext = _viewModel.Start };
        _startView.OpenRequested += async (_, _) => await ChooseAndOpenAsync().ConfigureAwait(true);
        _startView.PracticeFormRequested += async (_, _) => await OpenPracticeFormAsync().ConfigureAwait(true);
        _startView.RecentRequested += async item =>
        {
            if (item.Recovery is { } copy)
            {
                await RecoverAsync(copy).ConfigureAwait(true);
            }
            else if (item.Path is { } path)
            {
                await OpenPathsAsync([path]).ConfigureAwait(true);
            }
        };
        _startView.RecoverRequested += async copy => await RecoverAsync(copy).ConfigureAwait(true);
        _startView.SettingsRequested += (_, _) => ShowSettings();
        Host.Content = _startView;
    }

    /// <summary>Keeps this window's unsaved work as a recovery copy before an update restarts the app.</summary>
    /// <returns>Whether the work is safe to restart over: kept, or nothing unsaved.</returns>
    public async Task<bool> KeepForUpdateAsync() =>
        _viewModel.Editor is not { HasUnsavedChanges: true } editor || await editor.AutosaveAsync(isForUpdate: true).ConfigureAwait(true);

    /// <summary>Every window's work was kept: closing for the restart neither asks nor removes the copy.</summary>
    public void MarkKeptForUpdate()
    {
        if (_viewModel.Editor is { HasUnsavedChanges: true })
        {
            _isCloseConfirmed = true;
            _isKeptForUpdate = true;
        }
    }

    /// <summary>Reopens unsaved work from its recovery copy (FR-8.12): every item, unsaved, saving back where it was.</summary>
    public async Task RecoverAsync(RecoveryCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        byte[] bytes;
        try
        {
            bytes = RecoveryStore.Read(copy.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("The changes can’t be recovered", $"The recovery copy couldn’t be read. ({ex.Message})").ConfigureAwait(true);
            return;
        }

        if (await OpenFilesAsync([new InputFile(copy.Name, null, bytes)]).ConfigureAwait(true) is { } editor)
        {
            editor.MarkRecovered(copy);
        }
    }

    /// <summary>Settings (FS-706): one window, brought to the front if it's already open.</summary>
    public static void ShowSettings()
    {
        if (s_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        s_settingsWindow = new SettingsWindow();
        s_settingsWindow.Closed += (_, _) => s_settingsWindow = null;
        s_settingsWindow.Show();
    }

    private static SettingsWindow? s_settingsWindow;

    /// <summary>Shows a newly opened document, closing the one it replaces ("Open final copy instead", restoring edits).</summary>
    private void ReplaceEditor(EditorViewModel editor)
    {
        EditorViewModel? previous = _viewModel.Editor;
        _viewModel.ShowEditor(editor);
        ShowEditor(editor);
        previous?.DiscardRecoveryCopy();
        previous?.Dispose();
        _ = editor.LoadAsync();
    }

    // ----- Closing (D4, FR-8.11) -----

    private bool _isCloseConfirmed;
    private bool _isKeptForUpdate;

    /// <summary>Closes without asking about unsaved changes. Tests use this to tidy up.</summary>
    internal void CloseWithoutAsking()
    {
        _isCloseConfirmed = true;
        Close();
    }

    // ----- Window size and place -----

    /// <summary>The standard window size, for a first start and View → Reset layout.</summary>
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 860;

    /// <summary>
    /// New windows open where and as big as the last one was when it closed, so people don't size it again each time.
    /// A window opened while another is open steps down and right a little, so it isn't hidden exactly behind it; one
    /// left on a screen that's gone is moved back on screen when it opens (WindowFit).
    /// </summary>
    private void RestorePlacement()
    {
        if (Platform.AppSettings.Load().Window is not { } placement)
        {
            return;
        }

        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        int cascade = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.OfType<MainWindow>().Count(window => window != this && window.IsVisible) * 28
            : 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(placement.X + cascade, placement.Y + cascade);
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Remembers this window's size and place for the next one. A minimised window keeps the last good ones.</summary>
    private void RememberPlacement()
    {
        if (WindowState == WindowState.Minimized || Width <= 0 || Height <= 0)
        {
            return;
        }

        bool isMaximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowPlacement? earlier = Platform.AppSettings.Load().Window;

        // A maximised window's own size is the screen's; keep the size it had before, to go back to.
        var placement = isMaximized && earlier is not null
            ? earlier with { IsMaximized = true }
            : new WindowPlacement(Width, Height, Position.X, Position.Y, isMaximized);
        Platform.AppSettings.Update(settings => settings with { Window = placement });
    }

    /// <summary>View → Reset layout: the window back at its standard size, in the middle of its screen.</summary>
    public void ResetWindowSize()
    {
        WindowState = WindowState.Normal;
        Width = DefaultWidth;
        Height = DefaultHeight;
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            PixelRect area = screen.WorkingArea;
            var size = PixelSize.FromSize(new Size(Width, Height), screen.Scaling);
            Position = new PixelPoint(area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2));
        }

        WindowFit.KeepOnScreen(this);
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

    /// <summary>
    /// Runs a command the way its button does: not while it's already running, so pressing Mod+E twice doesn't make two
    /// exports. Calling ExecuteAsync directly would skip that check.
    /// </summary>
    private static void RunOnce(System.Windows.Input.ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
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

        if (editor is { IsOrganising: false, TypingItem: null } && !isTyping && ToolKeys.IsOn
            && ToolKeys.ToolFor(e.Key, e.KeyModifiers) is { } tool)
        {
            RunOnce(editor.SelectToolCommand, tool);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.B when hasCommand && editor is not null && editor.OptionsHaveTextStyle:
                // Bold works while typing too, so it's caught here rather than by the text box (FS-952).
                editor.ToggleBoldCommand.Execute(null);
                break;
            case Key.O when hasCommand:
                _ = ChooseAndOpenAsync();
                break;
            case Key.S when hasCommand && hasShift && editor is not null:
                RunOnce(editor.SaveAsCommand);
                break;
            case Key.S when hasCommand && editor is not null:
                RunOnce(editor.SaveCommand);
                break;
            case Key.E when hasCommand && editor is not null:
                RunOnce(editor.ExportCommand);
                break;
            case Key.P when hasCommand && editor is not null:
                RunOnce(editor.PrintCommand);
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
                _ = PutOnClipboardAsync(editor.CopyToClipboard());
                break;
            case Key.X when hasCommand && editor is not null && !isTyping:
                _ = CutAsync(editor);
                break;
            case Key.V when hasCommand && editor is not null && !isTyping:
                _ = PasteAsync(editor);
                break;
            case Key.OemComma when hasCommand:
                ShowSettings();
                break;
            case Key.W when hasCommand:
                Close();
                break;
            case Key.F6 when _editorView is not null:
                // Toolbar → page list → page → status bar (INTERACTION-PATTERNS §11).
                _editorView.FocusNextArea(backwards: hasShift);
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

    // ----- System clipboard (FS-702) -----

    private static readonly DataFormat<byte[]> s_itemsFormat = DataFormat.CreateBytesApplicationFormat(EditorViewModel.ItemsFormat);

    /// <summary>Puts copied items on the system clipboard: Fillsign's format, plus their text for other apps.</summary>
    private async Task PutOnClipboardAsync(SystemClip? clip)
    {
        if (clip is null || Clipboard is not { } clipboard)
        {
            return;
        }

        var item = new DataTransferItem();
        if (clip.Items is { } items)
        {
            item.Set(s_itemsFormat, items);
        }

        if (clip.Text is { } text)
        {
            item.SetText(text);
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
    }

    private async Task CutAsync(EditorViewModel editor) => await PutOnClipboardAsync(await editor.CutToClipboardAsync().ConfigureAwait(true)).ConfigureAwait(true);

    /// <summary>Pastes Fillsign items, or another app's text; says so when the clipboard holds something else (FR-7.14).</summary>
    internal async Task PasteAsync(EditorViewModel editor)
    {
        byte[]? items = null;
        string? text = null;
        byte[]? picture = null;
        bool hasOther = false;
        if (!editor.IsOrganising && Clipboard is { } clipboard && await clipboard.TryGetDataAsync().ConfigureAwait(true) is { } data)
        {
            using (data as IDisposable)
            {
                items = data.Contains(s_itemsFormat) ? await data.TryGetValueAsync(s_itemsFormat).ConfigureAwait(true) : null;
                text = items is null ? await data.TryGetTextAsync().ConfigureAwait(true) : null;

                // Text wins when both are there: apps like Word put a picture of the copied text alongside it.
                if (items is null && string.IsNullOrWhiteSpace(text) && data.Contains(DataFormat.Bitmap)
                    && await data.TryGetBitmapAsync().ConfigureAwait(true) is { } bitmap)
                {
                    using (bitmap)
                    {
                        using var png = new MemoryStream();
                        bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        picture = png.ToArray();
                    }
                }

                hasOther = data.Formats.Count > 0;
            }
        }

        bool isPasted = picture is not null ? editor.PastePicture(picture) : editor.PasteFromClipboard(items, text);
        if (!isPasted && hasOther && !editor.IsOrganising)
        {
            await ShowMessageAsync(
                "That can’t be pasted",
                "Fillsign can paste its own items, text and pictures from other apps. What’s on the clipboard isn’t one of those.").ConfigureAwait(true);
        }
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
                // Pages are copied and pasted within this document (FR-7.1, 7.2); the window's handler does it.
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// FS-716: on the page, Tab and Shift+Tab select the next or previous item in reading order, so items can be
    /// chosen without a mouse. Past the last item (or before the first), Tab moves focus on as usual.
    /// </summary>
    private void OnTabOnPage(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || DialogLayer.IsVisible || _viewModel.Editor is not { } editor || editor.TypingItem is not null
            || editor.EditingField is not null || !IsOnPage(editor, isTyping: false))
        {
            return;
        }

        // Once a form field is in use, Tab goes from field to field (FS-950); past the last of the items, into the fields.
        bool backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isMoved = editor.FocusedField is not null
            ? editor.MoveFieldFocus(backwards)
            : editor.SelectNextItem(backwards) || editor.MoveFieldFocus(backwards);
        if (isMoved)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keys for the form field Tab reached (FS-950), caught on the way down so the page's scroll view doesn't take the
    /// arrow keys or Space first. Space or Enter fills the field as a click would; the arrow keys move within it: the
    /// next radio button, another choice, another row. In a multi-select list, ⌘/Ctrl+arrows move without choosing
    /// and Shift+arrows extend, as in a browser.
    /// </summary>
    private void OnFieldKeys(object? sender, KeyEventArgs e)
    {
        if (DialogLayer.IsVisible || _viewModel.Editor is not { FocusedField: { } focused } editor
            || FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        if (e.Key is Key.Space or Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = editor.ActivateFocusedField();
            return;
        }

        KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        int step = e.Key switch { Key.Up or Key.Left => -1, Key.Down or Key.Right => 1, _ => 0 };
        ListKeys? listKeys = e.KeyModifiers switch
        {
            KeyModifiers.None => ListKeys.Choose,
            KeyModifiers.Shift => ListKeys.Extend,
            _ when e.KeyModifiers == command => ListKeys.MoveOnly,
            _ => null,
        };
        if (step != 0 && listKeys is { } keys && (keys == ListKeys.Choose || focused.Field.IsMultiSelect))
        {
            e.Handled = editor.MoveWithinField(step, keys);
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

    /// <summary>A message that's just information, such as About: no warning icon.</summary>
    public async Task ShowInformationAsync(string title, string message) =>
        await ShowDialogAsync(new MessageDialog(title, [message], "Got it", MessageTone.Information), dialog => dialog.Closed.ContinueWith(_ => true, TaskScheduler.Default)).ConfigureAwait(true);

    /// <summary>Saved signatures and initials. Tests point this at a temporary folder.</summary>
    internal static SavedSignatureStore? SignatureStoreOverride { get; set; }

    internal static SavedSignatureStore SignatureStore => SignatureStoreOverride ?? s_defaultSignatureStore.Value;

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

    public Task<ExportDecision?> AskExportAsync(ExportRequest request) =>
        ShowDialogAsync(new ExportDialog(request), dialog => dialog.Result, top: 64);

    /// <summary>How to go on with a file whose owner protected it against changes (FS-214, FS-018).</summary>
    public enum RestrictedChoice
    {
        DontOpen,
        ViewOnly,
        EditAnyway,
    }

    /// <summary>
    /// FS-214: the owner of these files restricted editing. Ask before going on; never remove the restriction quietly.
    /// View only is the safe default.
    /// </summary>
    public async Task<RestrictedChoice> AskAboutRestrictedAsync(IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        string which = fileNames.Count == 1 ? fileNames[0] : $"{fileNames.Count} of these files";
        Choice choice = await ShowDialogAsync(
            new ChoiceDialog(
                $"{(fileNames.Count == 1 ? which : "These files")} can’t be changed",
                $"The owner of {which} set it so it shouldn’t be changed. You can view it, or edit it anyway if you’re allowed to fill it in. Copies you save or export from Fillsign won’t carry the restriction.",
                "View only",
                otherLabel: "Edit anyway",
                focusMain: true,
                cancelLabel: "Don’t open"),
            dialog => dialog.Result).ConfigureAwait(true);
        return choice switch
        {
            Choice.Main => RestrictedChoice.ViewOnly,
            Choice.Other => RestrictedChoice.EditAnyway,
            _ => RestrictedChoice.DontOpen,
        };
    }

    public async Task<bool> ConfirmAddRestrictedAsync(IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        string which = fileNames.Count == 1 ? fileNames[0] : $"{fileNames.Count} of these files";
        Choice choice = await ShowDialogAsync(
            new ChoiceDialog(
                "Add pages that can’t be changed?",
                $"The owner of {which} set it so it shouldn’t be changed. Adding its pages copies them into this document without the restriction.",
                "Add pages anyway",
                otherLabel: null,
                focusMain: false,
                cancelLabel: "Don’t add"),
            dialog => dialog.Result).ConfigureAwait(true);
        return choice == Choice.Main;
    }

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

    public PrintOutcome PrintFile(string path) => Printing.Print(path, this);

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
    /// <summary>
    /// Once the dialog is laid out, puts focus in it unless the dialog already chose where (a text box, the default
    /// button): it can't be focused while it's still being attached.
    /// </summary>
    private static void FocusInside(Control dialog) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (dialog.IsKeyboardFocusWithin)
                {
                    return;
                }

                Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<InputElement>().FirstOrDefault(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible)?.Focus(NavigationMethod.Tab);
            },
            Avalonia.Threading.DispatcherPriority.Loaded);

    // Below this distance from the window's bottom, a dialog moves up and its middle scrolls (FS-709, S5).
    private const double DialogBottomGap = 16;

    private double _dialogTop = DefaultDialogTop;

    /// <summary>
    /// Keeps the dialog inside the window: on a short window it starts nearer the top and is no taller than the space
    /// left, so tall dialogs scroll their middle and keep their buttons in reach.
    /// </summary>
    private void FitDialogToWindow()
    {
        double height = Bounds.Height;
        double top = height < 760 ? Math.Min(_dialogTop, DialogBottomGap) : _dialogTop;
        DialogHost.Margin = new Thickness(0, top, 0, 0);
        DialogHost.MaxHeight = Math.Max(200, height - top - DialogBottomGap);
    }

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
            _dialogTop = top;
            FitDialogToWindow();
            DialogLayer.IsVisible = true;
            DialogHost.Content = dialog;

            // Modal: the window under the dim can't be reached by Tab or clicked, so the dialog keeps focus and its
            // Esc and Enter keep working.
            Host.IsEnabled = false;
            FocusInside(dialog);
            return await result(dialog).ConfigureAwait(true);
        }
        finally
        {
            Host.IsEnabled = true;
            DialogLayer.IsVisible = false;
            DialogHost.Content = null;
            previousFocus?.Focus();
            done.SetResult();
        }
    }
}
