using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Chainpen.App.Platform;
using Chainpen.App.Platform.Signatures;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;

using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>Phase 7: recent files, recovery, settings, the system clipboard, menus and keyboard areas.</summary>
public sealed class DesktopFitTests : IDisposable
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "chainpen-tests", Guid.NewGuid().ToString("N"));

    public DesktopFitTests()
    {
        // Each test starts with no recent files and no recovery copies.
        Directory.CreateDirectory(_folder);
        AppSettings.Update(settings => settings with { RecentFiles = [], Appearance = AppearanceChoice.MatchSystem });
        RecoveryStore.DeleteAll();
        MainWindow.SavePathOverride = (name, _) => Path.Combine(_folder, name);
    }

    public void Dispose()
    {
        MainWindow.SavePathOverride = null;
        RecoveryStore.DeleteAll();
        Directory.Delete(_folder, recursive: true);
    }

    [AvaloniaFact]
    public async Task Opened_documents_are_listed_in_Recent_and_the_list_can_be_cleared()
    {
        string copy = Path.Combine(_folder, "lease.pdf");
        File.Copy(TestFiles.Sample("sample-form.pdf"), copy);
        using (var opened = await Session.OpenAsync(copy))
        {
        }

        using var s = await Session.StartAsync();
        RecentItemViewModel row = Assert.Single(s.Start.Recent);
        Assert.Equal("lease.pdf", row.Name);
        Assert.False(row.HasStatus);
        await Pump(() => true);
        Save(s.Window, "phase7-2-recent");

        s.Start.ClearRecent();
        Assert.Empty(s.Start.Recent);
        Assert.Empty(AppSettings.Load().Recent);
    }

    [AvaloniaFact]
    public async Task Unsaved_work_left_by_a_crash_is_offered_back_with_every_item()
    {
        // A window autosaves, then "crashes": it's never closed, so its recovery copy stays, and nothing has it open.
        var crashed = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        crashed.AddTick();
        await crashed.Editor.AutosaveAsync();
        Assert.True(crashed.Editor.HasRecoveryStatus);
        Assert.StartsWith("Recovery copy kept ", crashed.Editor.RecoveryStatus, StringComparison.Ordinal);
        RecoveryCopy kept = Assert.Single(RecoveryStore.List());
        RecoveryStore.Release(kept.Id);

        using var s = await Session.StartAsync();
        Assert.True(s.Start.HasRecovery);
        Assert.Contains("sample-form.pdf", s.Start.RecoveryText, StringComparison.Ordinal);
        Assert.Equal("Changes not saved · open to recover", s.Start.Recent[0].Status);
        await Pump(() => true);
        Save(s.Window, "phase7-2-recovery");

        await s.Window.RecoverAsync(kept);
        EditorViewModel recovered = Editor(s.Window);
        Assert.IsType<MarkItem>(Assert.Single(recovered.State.Items));
        Assert.True(recovered.HasUnsavedChanges);
        Assert.StartsWith("Your unsaved changes from ", recovered.RestoredNotice, StringComparison.Ordinal);

        // Saving it keeps it in the file, so the recovery copy goes.
        Assert.True(await recovered.SaveAsync());
        Assert.Empty(RecoveryStore.List());
        crashed.Window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Not_now_keeps_the_work_recoverable_from_Recent_without_asking_again()
    {
        var crashed = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        crashed.AddTick();
        await crashed.Editor.AutosaveAsync();
        RecoveryStore.Release(crashed.Editor.RecoveryId);

        using var s = await Session.StartAsync();
        s.Start.PutOffRecovery();
        Assert.False(s.Start.HasRecovery);
        Assert.Equal("Changes not saved · open to recover", s.Start.Recent[0].Status);
        Assert.True(Assert.Single(RecoveryStore.List()).IsDismissed);
        crashed.Window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task A_recovery_copy_that_belongs_to_an_open_document_isnt_offered_or_recovered()
    {
        // One window has unsaved work and its recovery copy; a second window opens on the start screen.
        using var working = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        working.AddTick();
        await working.Editor.AutosaveAsync();
        RecoveryCopy copy = Assert.Single(RecoveryStore.List());
        Assert.Equal(Environment.ProcessId, copy.OwnerProcessId);
        Assert.True(RecoveryStore.IsInUse(copy));

        using var s = await Session.StartAsync();
        Assert.False(s.Start.HasRecovery);
        Assert.DoesNotContain(s.Start.Recent, item => item.Recovery is not null);

        // Asked to recover it anyway (a list made before it was taken), nothing opens and the copy stays the first window's.
        await s.Window.RecoverAsync(copy);
        Assert.Null(((MainWindowViewModel)s.Window.DataContext!).Editor);
        Assert.Single(RecoveryStore.List());
    }

    [AvaloniaFact]
    public async Task A_recovery_copy_held_by_another_running_Chainpen_is_left_alone_until_that_one_has_gone()
    {
        // A copy written by "another Chainpen": another process that's running now stands in for it.
        using System.Diagnostics.Process other = LongestRunningOtherProcess();
        var written = new RecoveryCopy(Guid.NewGuid(), "theirs.pdf", null, null, DateTimeOffset.Now.AddDays(-40));
        Assert.True(RecoveryStore.Write(written, File.ReadAllBytes(TestFiles.Sample("sample-form.pdf"))));
        string info = Path.Combine(RecoveryStore.Folder, $"{written.Id:N}.json");
        void SetOwner(int processId, DateTimeOffset started)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(info))!;
            json["OwnerProcessId"] = processId;
            json["OwnerStartedAt"] = started;
            File.WriteAllText(info, json.ToJsonString());
        }

        SetOwner(other.Id, other.StartTime.ToUniversalTime());
        RecoveryCopy theirs = Assert.Single(RecoveryStore.List());
        Assert.True(RecoveryStore.IsInUse(theirs));

        // Not offered here, and the clean-up of old copies doesn't take a live document's only copy.
        using var s = await Session.StartAsync();
        Assert.False(s.Start.HasRecovery);
        Assert.Empty(s.Start.Recent);
        RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(30), DateTimeOffset.Now);
        Assert.Single(RecoveryStore.List());

        // The same process number with another start time is some other program: that Chainpen has gone, so its work
        // is offered back.
        SetOwner(other.Id, other.StartTime.ToUniversalTime().AddHours(-3));
        Assert.False(RecoveryStore.IsInUse(Assert.Single(RecoveryStore.List())));
        s.Start.Refresh();
        Assert.True(s.Start.HasRecovery);
    }

    /// <summary>
    /// The process that has been running longest, of those this user can look at. macOS lists the newest process first,
    /// which is often a helper that exits within the test; that reads as the other Chainpen having gone.
    /// </summary>
    private static System.Diagnostics.Process LongestRunningOtherProcess()
    {
        System.Diagnostics.Process? oldest = null;
        DateTime oldestStart = DateTime.MaxValue;
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (process.Id != Environment.ProcessId && !process.HasExited && process.StartTime is var started
                    && started > DateTime.MinValue && started < oldestStart)
                {
                    oldest?.Dispose();
                    oldest = process;
                    oldestStart = started;
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // One this user can't look at.
            }

            process.Dispose();
        }

        return oldest ?? throw new InvalidOperationException("No other process to stand in for another Chainpen.");
    }

    [AvaloniaFact]
    public async Task Closing_without_saving_removes_the_recovery_copy()
    {
        var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        s.AddTick();
        await s.Editor.AutosaveAsync();
        Assert.Single(RecoveryStore.List());

        s.Window.Close();
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Click(s.Dialog<ChoiceDialog>()!, "Don’t save");
        await Pump(() => !s.Window.IsVisible);
        Assert.Empty(RecoveryStore.List());
    }

    [AvaloniaFact]
    public async Task Settings_change_the_appearance_and_recovery_period_straight_away()
    {
        // Tests share one settings file and run in any order, so start from the defaults this test checks.
        AppSettings.Update(_ => new AppSettings());
        var window = new SettingsWindow();
        window.Show();
        SettingsViewModel settings = window.ViewModel;
        Assert.True(settings.IsMatchSystem);
        Assert.True(settings.DontSendCrashReports);

        settings.IsDark = true;
        await Pump(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Dark);
        Assert.Equal(AppearanceChoice.Dark, AppSettings.Load().Appearance);
        await Pump(() => true);
        Save(window, "phase7-7-settings-dark");

        settings.IsSevenDays = true;
        Assert.Equal(7, AppSettings.Load().RecoveryDays);
        settings.IsThirtyDays = true;
        Assert.Equal(AppSettings.DefaultRecoveryDays, AppSettings.Load().RecoveryDays);

        settings.IsMatchSystem = true;
        await Pump(() => Application.Current!.RequestedThemeVariant == ThemeVariant.Default);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_recovery_copies_asks_first_and_open_documents_keep_a_fresh_one()
    {
        var crashed = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        crashed.AddTick();
        await crashed.Editor.AutosaveAsync();

        // A copy left by a document that isn't open any more.
        var leftOver = new RecoveryCopy(Guid.NewGuid(), "old.pdf", null, null, DateTimeOffset.Now, IsForUpdate: false);
        Assert.True(RecoveryStore.Write(leftOver, File.ReadAllBytes(TestFiles.Sample("sample-form.pdf"))));

        var window = new SettingsWindow();
        window.Show();
        Task deleting = window.ViewModel.DeleteRecoveryCopiesCommand.ExecuteAsync(null);
        ChoiceDialog dialog = await Await(() => window.FindControl<ContentControl>("DialogHost")!.Content as ChoiceDialog);
        Assert.Equal("Delete all recovery copies?", dialog.FindControl<TextBlock>("Heading")!.Text);
        Click(dialog, "Delete recovery copies");
        await deleting;
        Assert.DoesNotContain(RecoveryStore.List(), copy => copy.Id == leftOver.Id);

        // The open document still has unsaved work, so it isn't left without a copy until its next change.
        await Pump(() => RecoveryStore.List().Any(copy => copy.Id == crashed.Editor.RecoveryId));
        window.Close();
        crashed.Window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Removing_a_saved_signature_in_Settings_asks_first()
    {
        var store = new SavedSignatureStore(Path.Combine(_folder, "signatures"), new NoKeyVault());
        store.Keep(new SavedSignature(new SignatureImage(Guid.NewGuid(), [.. Convert.FromBase64String(OnePixelPng)], 1, 1), SignatureKind.Signature, SignatureMethod.Typed, DateTimeOffset.UtcNow));
        MainWindow.SignatureStoreOverride = store;
        try
        {
            var window = new SettingsWindow();
            window.Show();
            await Pump(() => window.ViewModel.HasSaved);
            SavedRowViewModel row = Assert.Single(window.ViewModel.Saved);
            Assert.StartsWith("Signature (typed) · last used ", row.Description, StringComparison.Ordinal);
            await Pump(() => true);
            Save(window, "phase7-7-settings");

            Task removing = window.ViewModel.RemoveSignatureCommand.ExecuteAsync(row);
            ChoiceDialog dialog = await Await(() => window.FindControl<ContentControl>("DialogHost")!.Content as ChoiceDialog);
            Click(dialog, "Remove signature");
            await removing;
            Assert.Empty(store.Load());
            window.Close();
        }
        finally
        {
            MainWindow.SignatureStoreOverride = null;
        }
    }

    [AvaloniaFact]
    public async Task Copied_items_paste_into_another_window_and_text_from_other_apps_pastes_as_text()
    {
        using var first = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        first.AddTick();
        first.Editor.SelectAllOnCurrentPage();
        first.Key(Key.C, s_command);
        await Pump(() => true);

        using var second = await Session.OpenAsync(TestFiles.Sample("four-pages.pdf"));
        second.Key(Key.V, s_command);
        await Pump(() => second.Editor.State.Items.Count == 1);
        Assert.IsType<MarkItem>(second.Editor.State.Items[0]);
        Assert.Equal(second.Editor.State.Pages[0].Id, second.Editor.State.Items[0].Page);

        // Text copied in another app.
        await second.Window.Clipboard!.SetTextAsync("Jordan Lee\n12 High Street");
        second.Key(Key.V, s_command);
        await Pump(() => second.Editor.State.Items.Count == 2);
        TextItem pasted = Assert.IsType<TextItem>(second.Editor.State.Items[1]);
        Assert.Equal("Jordan Lee\n12 High Street", pasted.Text);
        Assert.Equal([pasted.Id], second.Editor.SelectedIds);
    }

    [AvaloniaFact]
    public async Task Pages_copy_and_paste_with_their_edits_after_the_selection()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        s.AddTick();
        s.Editor.ShowOrganiseCommand.Execute(null);
        s.Editor.SelectAllPagesCommand.Execute(null);
        s.Key(Key.C, s_command);
        s.Key(Key.V, s_command);
        await Pump(() => s.Editor.State.Pages.Length == 4);

        // Scenario 8: four pages, the copies' edits included, the copies selected.
        Assert.Equal(2, s.Editor.State.Items.Count);
        Assert.Equal([s.Editor.State.Pages[2].Id, s.Editor.State.Pages[3].Id], s.Editor.SelectedPages.OrderBy(id => s.Editor.State.IndexOf(id)));
        Assert.Equal(s.Editor.State.Pages[2].Id, s.Editor.State.Items[1].Page);
    }

    [AvaloniaFact]
    public async Task The_menus_cover_every_area_and_wait_for_a_document()
    {
        using var s = await Session.StartAsync();
        NativeMenu menu = NativeMenu.GetMenu(s.Window)!;
        Assert.Equal(["File", "Edit", "View", "Page", "Help"], menu.Items.OfType<NativeMenuItem>().Select(item => item.Header));
        NativeMenuItem save = Find(menu, "File", "Save");
        Assert.False(save.IsEnabled);
        Assert.True(Find(menu, "File", "Open…").IsEnabled);

        await s.Window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
        await Pump(() => save.IsEnabled);
        Assert.Equal(new KeyGesture(Key.S, s_command == RawInputModifiers.Meta ? KeyModifiers.Meta : KeyModifiers.Control), save.Gesture);

        // About and Settings: in the Chainpen app menu on macOS (not "About Avalonia"), in File and Help elsewhere.
        IEnumerable<object?> Headers(string top) =>
            menu.Items.OfType<NativeMenuItem>().First(item => (string?)item.Header == top).Menu!.Items.OfType<NativeMenuItem>().Select(item => item.Header);
        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain("Settings…", Headers("File"));
            Assert.DoesNotContain("About Chainpen", Headers("Help"));
            NativeMenu appMenu = App.MacAppMenu(() => s.Window);
            Assert.Equal(["About Chainpen", "Settings…", "Check for updates…"], appMenu.Items.OfType<NativeMenuItem>().Where(item => item is not NativeMenuItemSeparator).Select(item => item.Header));
        }
        else
        {
            Assert.Contains("Settings…", Headers("File"));
            Assert.Contains("About Chainpen", Headers("Help"));
            Assert.Contains("Check for updates…", Headers("Help"));
        }
    }

    [Fact]
    public void Cleaning_up_recovery_copies_also_removes_files_with_no_readable_description()
    {
        // Each holds document contents: a copy whose description was never written, one whose description is damaged,
        // and a half-written file from a crash mid-write.
        Directory.CreateDirectory(RecoveryStore.Folder);
        string[] strays = [.. new[] { $"{Guid.NewGuid():N}.pdf", $"{Guid.NewGuid():N}.json", $"{Guid.NewGuid():N}.pdf.tmp" }
            .Select(name => Path.Combine(RecoveryStore.Folder, name))];
        foreach (string stray in strays)
        {
            File.WriteAllText(stray, "contents");
        }

        var kept = new RecoveryCopy(Guid.NewGuid(), "kept.pdf", null, null, DateTimeOffset.Now);
        Assert.True(RecoveryStore.Write(kept, File.ReadAllBytes(TestFiles.Sample("sample-form.pdf"))));

        // Recent strays might still be being written, so the age limit applies to them as it does to copies.
        RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(1), DateTimeOffset.Now);
        Assert.All(strays, stray => Assert.True(File.Exists(stray)));

        foreach (string stray in strays)
        {
            File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-2));
        }

        RecoveryStore.DeleteOlderThan(TimeSpan.FromDays(1), DateTimeOffset.Now);
        Assert.All(strays, stray => Assert.False(File.Exists(stray)));
        Assert.Single(RecoveryStore.List());

        // Delete all means everything.
        File.WriteAllText(strays[0], "contents");
        RecoveryStore.DeleteAll();
        Assert.Empty(Directory.EnumerateFiles(RecoveryStore.Folder));
    }

    [Fact]
    public void Settings_are_only_readable_by_this_user()
    {
        // Recent lists document names and folders.
        AppSettings.Update(settings => settings with { RecentFiles = [] });
        string path = Path.Combine(AppSettings.Folder, "settings.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void A_print_copy_is_only_ever_readable_by_this_user()
    {
        string path = Printing.WriteCopy("lease.pdf", DateTime.Now, File.ReadAllBytes(TestFiles.Sample("form-fields.pdf")));
        try
        {
            Assert.StartsWith(Printing.Folder, path, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            // Old copies go; recent ones, possibly still printing, stay.
            Printing.ClearOldCopies(DateTime.UtcNow);
            Assert.True(File.Exists(path));
            Printing.ClearOldCopies(DateTime.UtcNow.AddDays(2));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Check_for_updates_says_what_it_found()
    {
        Assert.Equal("You’re up to date", AppUpdates.Describe(UpdateCheck.UpToDate, "1.2.0", null).Title);
        Assert.Contains("1.3.0", AppUpdates.Describe(UpdateCheck.Ready, "1.2.0", new UpdateReady("1.3.0", [])).Message, StringComparison.Ordinal);
        Assert.Contains("can’t update itself", AppUpdates.Describe(UpdateCheck.NotInstalled, "1.2.0", null).Message, StringComparison.Ordinal);
        Assert.Equal("Couldn’t check for updates", AppUpdates.Describe(UpdateCheck.Failed, "1.2.0", null).Title);
    }

    [Fact]
    public void Updates_come_from_GitHub_unless_a_test_folder_is_named()
    {
        Assert.IsType<Velopack.Sources.GithubSource>(AppUpdates.CreateSource(null));
        Assert.IsType<Velopack.Sources.GithubSource>(AppUpdates.CreateSource(" "));
        Assert.IsType<Velopack.Sources.SimpleFileSource>(AppUpdates.CreateSource(Path.GetTempPath()));
    }

    [AvaloniaFact]
    public async Task Check_for_updates_from_source_explains_this_copy_cant_update()
    {
        using var s = await Session.StartAsync();
        AppUpdates.Instance.ShowReady(null); // Another test may have shown one.
        Task checking = s.Window.CheckForUpdatesAsync();
        await Pump(() => s.Dialog<MessageDialog>() is not null);
        await Pump(() => true);
        string shown = string.Join(" ", s.Dialog<MessageDialog>()!.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.True(shown.Contains("can’t update itself", StringComparison.Ordinal), shown);
        Click(s.Dialog<MessageDialog>()!, "Got it");
        await checking;
    }

    [AvaloniaFact]
    public async Task F6_moves_between_the_toolbar_page_list_page_and_status_bar()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        EditorView view = (EditorView)s.Window.FindControl<ContentControl>("Host")!.Content!;
        IInputElement? Focused() => s.Window.FocusManager?.GetFocusedElement();
        bool IsIn(string name) => Focused() is Visual visual && view.GetVisualDescendants().OfType<Control>().First(c => c.Name == name) is { } area
            && (ReferenceEquals(area, visual) || area.IsVisualAncestorOf(visual));

        var seen = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            s.Key(Key.F6, RawInputModifiers.None);
            await Pump(() => true);
            seen.Add(IsIn("Tools") ? "toolbar" : IsIn("PageList") ? "pages" : IsIn("Scroller") ? "page" : IsIn("PageBox") ? "status" : "?");
        }

        Assert.Equal(4, seen.Distinct().Count());
        Assert.DoesNotContain("?", seen);
    }

    [AvaloniaFact]
    public void The_shortcut_list_uses_this_computers_command_key()
    {
        string expected = OperatingSystem.IsMacOS() ? "⌘ S" : "Ctrl S";
        Assert.Equal(expected, ShortcutsPanel.ForThisComputer("Mod S"));
        Assert.Contains(ShortcutsPanel.Shortcuts, shortcut => shortcut.Keys.Contains("F6"));
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static NativeMenuItem Find(NativeMenu menu, string top, string header) =>
        menu.Items.OfType<NativeMenuItem>().First(item => (string?)item.Header == top).Menu!.Items.OfType<NativeMenuItem>().First(item => (string?)item.Header == header);

    private static async Task<T> Await<T>(Func<T?> find)
        where T : class
    {
        await Pump(() => find() is not null);
        return find()!;
    }

    private sealed class Session : IDisposable
    {
        private Session(MainWindow window)
        {
            Window = window;
        }

        public MainWindow Window { get; }

        public EditorViewModel Editor => UiTest.Editor(Window);

        public StartViewModel Start => ((MainWindowViewModel)Window.DataContext!).Start;

        public static async Task<Session> StartAsync()
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await Pump(() => true);
            return new Session(window);
        }

        public static async Task<Session> OpenAsync(string path)
        {
            Session session = await StartAsync();
            await session.Window.OpenPathsAsync([path]);
            await Pump(() => session.Window.DataContext is MainWindowViewModel { Editor.IsLoading: false });
            return session;
        }

        public T? Dialog<T>()
            where T : Control => Window.FindControl<ContentControl>("DialogHost")!.Content as T;

        public void AddTick()
        {
            Editor.SelectToolCommand.Execute(EditorTool.Tick);
            Editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            Editor.PointerReleased(new PagePoint(100, 100));
            Editor.SelectToolCommand.Execute(EditorTool.Select);
        }

        public void Key(Key key, RawInputModifiers modifiers)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        public void Dispose() => Window.CloseWithoutAsking();
    }
}
