using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;
using Chainpen.Core.Session;
using Chainpen.Pdf.Pdfium;

using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>Phase 6: Save keeps the working copy, Export makes the final copy (FR-8, FR-9).</summary>
public sealed class SaveTests : IDisposable
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "chainpen-tests", Guid.NewGuid().ToString("N"));
    private int _savePathAsks;

    public SaveTests()
    {
        Directory.CreateDirectory(_folder);
        MainWindow.SavePathOverride = (name, _) =>
        {
            _savePathAsks++;
            return Path.Combine(_folder, name);
        };
    }

    public void Dispose()
    {
        MainWindow.SavePathOverride = null;
        Directory.Delete(_folder, recursive: true);
    }

    [AvaloniaFact]
    public async Task Saving_asks_where_once_then_saves_in_place_and_the_status_says_saved()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        Assert.Equal("Working copy · no changes yet", s.Editor.DocumentStatus);
        s.AddTick();
        Assert.Equal("Working copy · not saved to file", s.Editor.DocumentStatus);

        s.Key(Key.S, s_command);
        await Pump(() => !s.Editor.HasUnsavedChanges);
        string saved = Path.Combine(_folder, "sample-form.pdf");
        Assert.True(File.Exists(saved));
        Assert.Equal(1, _savePathAsks);
        Assert.StartsWith("Working copy · saved ", s.Editor.DocumentStatus, StringComparison.Ordinal);
        Assert.Equal(saved, s.Editor.WorkingCopyPath);
        Assert.Equal(SessionStatus.Intact, SessionStore.Find(File.ReadAllBytes(saved)).Status);

        // The second save writes there without asking.
        s.AddTick(200);
        s.Key(Key.S, s_command);
        await Pump(() => !s.Editor.HasUnsavedChanges);
        Assert.Equal(1, _savePathAsks);
        Assert.Equal(2, SessionStore.Find(File.ReadAllBytes(saved)).Record!.Document.Items.Count);
    }

    [AvaloniaFact]
    public async Task Reopening_a_working_copy_restores_every_item_and_says_so()
    {
        string saved = Path.Combine(_folder, "sample-form.pdf");
        using (var first = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf")))
        {
            first.AddTick();
            first.Editor.RotatePagesRightCommand.Execute(first.Editor.Pages[1]);
            Assert.True(await first.Editor.SaveAsync());
        }

        using var s = await Session.OpenAsync(saved);
        Assert.True(s.Editor.HasRestoredNotice);
        Assert.StartsWith("Your edits from ", s.Editor.RestoredNotice, StringComparison.Ordinal);
        Assert.False(s.Editor.HasUnsavedChanges);
        MarkItem tick = Assert.IsType<MarkItem>(Assert.Single(s.Editor.State.Items));
        Assert.Equal(1, s.Editor.State.Pages[1].Rotation.QuarterTurns);

        // The clean pages come back: page 2 has only the rotation it had in the original, and Chainpen's turn is in the
        // record again.
        using (PdfiumDocument original = PdfiumDocument.Open(File.ReadAllBytes(TestFiles.Sample("sample-form.pdf"))))
        using (PdfiumDocument clean = PdfiumDocument.Open(s.Editor.Source.Pdf.SaveCopy(removeSecurity: true)))
        {
            Assert.Equal(original.GetPageInfo(1).Rotation, clean.GetPageInfo(1).Rotation);
        }

        // Restored items are fully editable (FR-9.2).
        s.Editor.SelectToolCommand.Execute(EditorTool.Select);
        s.Editor.PointerPressed(0, new PagePoint(tick.X + (tick.Size / 2), tick.Y + (tick.Size / 2)), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        s.Editor.PointerReleased(new PagePoint(tick.X + (tick.Size / 2), tick.Y + (tick.Size / 2)));
        Assert.Equal([tick.Id], s.Editor.SelectedIds);
        await Pump(() => true);
        Save(s.Window, "phase6-5-restored");

        // "Open final copy instead" shows the finished pages with nothing to edit.
        s.Editor.OpenAsFinalCopyCommand.Execute(null);
        await Pump(() => !ReferenceEquals(Editor(s.Window), s.Editor));
        EditorViewModel finalView = Editor(s.Window);
        Assert.Empty(finalView.State.Items);
        Assert.False(finalView.HasRestoredNotice);
    }

    [AvaloniaFact]
    public async Task Open_final_copy_instead_asks_about_changes_made_since_opening()
    {
        string saved = Path.Combine(_folder, "sample-form.pdf");
        using (var first = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf")))
        {
            first.AddTick();
            Assert.True(await first.Editor.SaveAsync());
        }

        using var s = await Session.OpenAsync(saved);
        s.AddTick(200);
        Assert.True(s.Editor.HasUnsavedChanges);

        // Cancel keeps the document, its new tick and the button to try again.
        Task opening = s.Editor.OpenAsFinalCopyCommand.ExecuteAsync(null);
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Assert.Equal("Save changes to sample-form.pdf before closing?", s.Dialog<ChoiceDialog>()!.FindControl<TextBlock>("Heading")!.Text);
        Click(s.Dialog<ChoiceDialog>()!, "Cancel");
        await opening;
        Assert.Same(s.Editor, Editor(s.Window));
        Assert.Equal(2, s.Editor.State.Items.Count);
        Assert.True(s.Editor.HasUnsavedChanges);

        // Save writes the working copy first, so the final copy shown has both ticks and the file still has the record.
        opening = s.Editor.OpenAsFinalCopyCommand.ExecuteAsync(null);
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Click(s.Dialog<ChoiceDialog>()!, "Save");
        await opening;
        await Pump(() => !ReferenceEquals(Editor(s.Window), s.Editor));
        Assert.Empty(Editor(s.Window).State.Items);
        Assert.Equal(2, SessionStore.Find(File.ReadAllBytes(saved)).Record!.Document.Items.Count);
    }

    [AvaloniaFact]
    public async Task Restoring_Chainpen_edits_asks_about_changes_made_since_opening()
    {
        string saved = Path.Combine(_folder, "sample-form.pdf");
        using (var first = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf")))
        {
            first.AddTick();
            Assert.True(await first.Editor.SaveAsync());
        }

        using (PdfiumDocument other = PdfiumDocument.Open(File.ReadAllBytes(saved)))
        {
            other.SetRotation(0, 2);
            File.WriteAllBytes(saved, other.SaveIncremental());
        }

        using var s = await Session.OpenAsync(saved);
        s.AddTick(200);
        Task restoring = s.Editor.RestoreChangedEditsCommand.ExecuteAsync(null);
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Click(s.Dialog<ChoiceDialog>()!, "Restore edits");

        // A second question, about the tick added since: Cancel keeps it, and the offer to restore.
        await Pump(() => s.Dialog<ChoiceDialog>() is { } dialog && dialog.FindControl<TextBlock>("Heading")!.Text!.StartsWith("Save changes", StringComparison.Ordinal));
        Click(s.Dialog<ChoiceDialog>()!, "Cancel");
        await restoring;
        Assert.Same(s.Editor, Editor(s.Window));
        Assert.Single(s.Editor.State.Items);
        Assert.True(s.Editor.IsChangedElsewhere);
    }

    [AvaloniaFact]
    public async Task A_working_copy_changed_in_another_app_opens_as_it_is_and_restoring_is_a_choice()
    {
        string saved = Path.Combine(_folder, "sample-form.pdf");
        using (var first = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf")))
        {
            first.AddTick();
            Assert.True(await first.Editor.SaveAsync());
        }

        // Another app adds its own update after Chainpen's.
        using (PdfiumDocument other = PdfiumDocument.Open(File.ReadAllBytes(saved)))
        {
            other.SetRotation(0, 2);
            File.WriteAllBytes(saved, other.SaveIncremental());
        }

        using var s = await Session.OpenAsync(saved);
        Assert.True(s.Editor.IsChangedElsewhere);
        Assert.Empty(s.Editor.State.Items);
        Assert.Equal("Opened as it is now · Chainpen edits not restored", s.Editor.DocumentStatus);
        await Pump(() => true);
        Save(s.Window, "phase6-D8-changed-elsewhere");

        Task restoring = s.Editor.RestoreChangedEditsCommand.ExecuteAsync(null);
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Click(s.Dialog<ChoiceDialog>()!, "Restore edits");
        await restoring;
        await Pump(() => !ReferenceEquals(Editor(s.Window), s.Editor));

        EditorViewModel restored = Editor(s.Window);
        Assert.Single(restored.State.Items);
        Assert.True(restored.HasUnsavedChanges);
        Assert.Equal(saved, restored.WorkingCopyPath);
    }

    [AvaloniaFact]
    public async Task Exporting_makes_a_final_copy_with_no_record_and_offers_to_show_it()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        s.AddTick();
        s.Key(Key.E, s_command);
        await Pump(() => s.Dialog<ExportDialog>() is not null);
        ExportDialog dialog = s.Dialog<ExportDialog>()!;
        TextBox name = dialog.FindControl<TextBox>("FileName")!;
        Assert.Equal("sample-form-final.pdf", name.Text);

        // The name has focus with just the name selected, as in the system's save dialogs: typing keeps ".pdf".
        await Pump(() => name.IsFocused && name.SelectedText.Length > 0);
        Assert.Equal("sample-form-final", name.SelectedText);
        Assert.StartsWith("Final copy ", dialog.FindControl<TextBlock>("SizeText")!.Text, StringComparison.Ordinal);
        dialog.UseFolder(_folder);
        await Pump(() => true);
        Save(s.Window, "phase6-6-export");

        Click(dialog, "Export final copy");
        await Pump(() => s.Editor.HasExportedNotice);
        string final = Path.Combine(_folder, "sample-form-final.pdf");
        Assert.True(File.Exists(final));
        Assert.Equal(SessionStatus.None, SessionStore.Find(File.ReadAllBytes(final)).Status);
        Assert.StartsWith("sample-form-final.pdf (", s.Editor.Exported!.Text, StringComparison.Ordinal);

        // "Also save the working copy" was ticked, so the working copy was saved too.
        Assert.False(dialog.FindControl<CheckBox>("LockFields")!.IsVisible, "a form with no fields has nothing to lock");
        await Pump(() => !s.Editor.HasUnsavedChanges);
        Assert.True(File.Exists(Path.Combine(_folder, "sample-form.pdf")));
        await Pump(() => true);
        Save(s.Window, "phase6-8-exported");
    }

    [AvaloniaFact]
    public async Task Export_wont_take_the_name_of_the_documents_own_file()
    {
        // Opened from the test folder, then saved as a working copy beside it.
        string original = Path.Combine(_folder, "form.pdf");
        File.Copy(TestFiles.Sample("sample-form.pdf"), original);
        byte[] originalBytes = File.ReadAllBytes(original);
        using var s = await Session.OpenAsync(original);
        s.AddTick();
        MainWindow.SavePathOverride = (_, _) => Path.Combine(_folder, "form-working.pdf");
        Assert.True(await s.Editor.SaveAsync());
        string workingCopy = Path.Combine(_folder, "form-working.pdf");
        s.AddTick(200);

        s.Key(Key.E, s_command);
        await Pump(() => s.Dialog<ExportDialog>() is not null);
        ExportDialog dialog = s.Dialog<ExportDialog>()!;
        dialog.UseFolder(_folder);
        Control problem = dialog.FindControl<Control>("NameProblem")!;
        Assert.False(problem.IsVisible);

        // Either of its own names is refused as it's typed, with or without ".pdf", and Export does nothing.
        foreach (string name in (string[])["form-working.pdf", "form-working", "form.pdf"])
        {
            dialog.FindControl<TextBox>("FileName")!.Text = name;
            await Pump(() => problem.IsVisible);
            Assert.Contains("this document’s own file", dialog.FindControl<TextBlock>("NameProblemText")!.Text, StringComparison.Ordinal);
            Assert.False(dialog.FindControl<Control>("ReplaceNote")!.IsVisible);
            Click(dialog, "Export final copy");
            await Pump(() => true);
            Assert.Same(dialog, s.Dialog<ExportDialog>());
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(SessionStatus.Intact, SessionStore.Find(File.ReadAllBytes(workingCopy)).Status);

        // Another name goes ahead, and the working copy is saved with the new tick as usual.
        dialog.FindControl<TextBox>("FileName")!.Text = "form-to-send.pdf";
        await Pump(() => !problem.IsVisible);
        Click(dialog, "Export final copy");
        await Pump(() => s.Editor.HasExportedNotice);
        Assert.Equal(SessionStatus.None, SessionStore.Find(File.ReadAllBytes(Path.Combine(_folder, "form-to-send.pdf"))).Status);
        Assert.Equal(2, SessionStore.Find(File.ReadAllBytes(workingCopy)).Record!.Document.Items.Count);
    }

    [AvaloniaFact]
    public async Task A_filled_form_is_exported_with_its_fields_locked()
    {
        File.Copy(TestFiles.Sample("form-fields.pdf"), Path.Combine(_folder, "form.pdf"));
        using var s = await Session.OpenAsync(Path.Combine(_folder, "form.pdf"));
        s.Editor.SetField("Name", "Jordan Lee");
        s.Key(Key.E, s_command);
        await Pump(() => s.Dialog<ExportDialog>() is not null);
        ExportDialog dialog = s.Dialog<ExportDialog>()!;
        CheckBox locking = dialog.FindControl<CheckBox>("LockFields")!;
        Assert.True(locking.IsVisible);
        Assert.True(locking.IsChecked);

        // The opening line says what's sent, whichever way the lock is set.
        TextBlock lede = dialog.FindControl<TextBlock>("Lede")!;
        Assert.Contains("your answers can’t be changed", lede.Text, StringComparison.Ordinal);
        locking.IsChecked = false;
        Assert.Contains("the form’s fields stay editable", lede.Text, StringComparison.Ordinal);
        locking.IsChecked = true;
        dialog.UseFolder(_folder);
        Click(dialog, "Export final copy");
        await Pump(() => s.Editor.HasExportedNotice);

        using Pdf.Pdfium.PdfiumDocument final = Pdf.Pdfium.PdfiumDocument.Open(File.ReadAllBytes(Path.Combine(_folder, "form-final.pdf")));
        IReadOnlyList<Pdf.Pdfium.FormField> fields = final.GetFormFields(0);
        Assert.Equal("Jordan Lee", fields.Single(field => field.Name == "Name").Value);
        Assert.All(fields, field => Assert.True(field.IsReadOnly, $"{field.Name} should be locked"));
    }

    [AvaloniaFact]
    public async Task Save_from_the_keyboard_keeps_text_still_being_typed_in_a_form_field()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("form-fields.pdf"));
        await s.TypeIntoFieldAsync("Name", "Jordan Lee");
        s.Key(Key.S, s_command);
        string saved = Path.Combine(_folder, "form-fields.pdf");
        await Pump(() => File.Exists(saved) && !s.Editor.IsSaving && !s.Editor.HasUnsavedChanges);
        Assert.Equal("Jordan Lee", s.Editor.State.Fields["Name"]);
        Assert.Equal("Jordan Lee", SessionStore.Find(File.ReadAllBytes(saved)).Record!.Document.Fields["Name"]);
    }

    [AvaloniaFact]
    public async Task Export_from_the_keyboard_keeps_text_still_being_typed_in_a_form_field()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("form-fields.pdf"));
        await s.TypeIntoFieldAsync("Name", "Jordan Lee");
        s.Key(Key.E, s_command);
        await Pump(() => s.Dialog<ExportDialog>() is not null);
        ExportDialog dialog = s.Dialog<ExportDialog>()!;
        dialog.UseFolder(_folder);
        Click(dialog, "Export final copy");
        await Pump(() => s.Editor.HasExportedNotice);

        using PdfiumDocument final = PdfiumDocument.Open(File.ReadAllBytes(Path.Combine(_folder, "form-fields-final.pdf")));
        Assert.Equal("Jordan Lee", final.GetFormFields(0).Single(field => field.Name == "Name").Value);
    }

    [AvaloniaFact]
    public async Task Closing_asks_about_text_still_being_typed_in_a_form_field()
    {
        var s = await Session.OpenAsync(TestFiles.Sample("form-fields.pdf"));
        await s.TypeIntoFieldAsync("Name", "Jordan Lee");
        s.Window.Close();
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Assert.True(s.Window.IsVisible);
        Assert.Equal("Jordan Lee", s.Editor.State.Fields["Name"]);
        Click(s.Dialog<ChoiceDialog>()!, "Don’t save");
        await Pump(() => !s.Window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Closing_with_unsaved_changes_asks_and_Save_is_the_default()
    {
        var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        s.AddTick();
        s.Window.Close();
        await Pump(() => s.Dialog<ChoiceDialog>() is not null);
        Assert.True(s.Window.IsVisible);
        ChoiceDialog dialog = s.Dialog<ChoiceDialog>()!;
        Assert.Equal("Save changes to sample-form.pdf before closing?", dialog.FindControl<TextBlock>("Heading")!.Text);
        await Pump(() => s.Window.FocusManager?.GetFocusedElement() is Button { Content: "Save" });
        await Pump(() => true);
        Save(s.Window, "phase6-D4-close");

        Click(dialog, "Don’t save");
        await Pump(() => !s.Window.IsVisible);
        Assert.False(File.Exists(Path.Combine(_folder, "sample-form.pdf")));
    }

    [AvaloniaFact]
    public async Task The_Save_menu_reminds_to_send_the_final_copy_when_there_is_a_signature()
    {
        using var s = await Session.OpenAsync(TestFiles.Sample("sample-form.pdf"));
        Assert.False(s.Editor.ShowsSignatureReminder);
        s.Editor.BeginPlacingSignature(new SignatureImage(Guid.NewGuid(), [.. Convert.FromBase64String(OnePixelPng)], 1, 1), SignatureKind.Signature);
        s.Editor.PointerPressed(0, new PagePoint(200, 300), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        s.Editor.PointerReleased(new PagePoint(200, 300));
        Assert.True(s.Editor.ShowsSignatureReminder);
        Assert.EndsWith("contains your signature", s.Editor.DocumentStatus, StringComparison.Ordinal);
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private sealed class Session : IDisposable
    {
        private Session(MainWindow window)
        {
            Window = window;
            Editor = UiTest.Editor(window);
        }

        public MainWindow Window { get; }

        public EditorViewModel Editor { get; }

        public static async Task<Session> OpenAsync(string path)
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([path]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
            return new Session(window);
        }

        public T? Dialog<T>()
            where T : Control => Window.FindControl<ContentControl>("DialogHost")!.Content as T;

        public void AddTick(double x = 100)
        {
            Editor.SelectToolCommand.Execute(EditorTool.Tick);
            Editor.PointerPressed(0, new PagePoint(x, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            Editor.PointerReleased(new PagePoint(x, 100));
            Editor.SelectToolCommand.Execute(EditorTool.Select);
        }

        public void Key(Key key, RawInputModifiers modifiers)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        /// <summary>Clicks one of the PDF's text fields and types into its box, leaving the box open with focus.</summary>
        public async Task TypeIntoFieldAsync(string name, string text)
        {
            FormField field = Editor.Source.FormFields(0).Single(candidate => candidate.Name == name);
            var transform = new Core.Geometry.PageTransform(Editor.Source.GetPage(0), Rotation.None);
            PagePoint at = transform.ToDisplay((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Bottom + field.Rect.Top) / 2);
            Editor.PointerPressed(0, at, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            Editor.PointerReleased(at);
            await Pump(() => Window.FocusManager?.GetFocusedElement() is TextBox box && box.Classes.Contains("formField"));
            Window.KeyTextInput(text);
            Assert.Equal(name, Editor.EditingField?.Hit.Field.Name);
            Assert.False(Editor.State.Fields.ContainsKey(name));
        }

        public void Dispose() => Window.CloseWithoutAsking();
    }
}
