using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Chainpen.App.Controls;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;

using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>Phase 3: tools, items and selection, driven with real pointer and key input on the practice form.</summary>
public sealed class EditingTests(ITestOutputHelper output)
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task Tick_and_cross_show_a_preview_where_a_click_would_place_them()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        Assert.Null(e.Editor.MarkPreview);

        e.Move(100, 150);
        MarkItem preview = Assert.IsType<MarkItem>(e.Editor.MarkPreview);
        Assert.Equal(MarkKind.Tick, preview.Kind);
        await Pump(() => true);
        Save(e.Window, "editing-tick-preview");

        // The click lands exactly where the preview showed.
        e.Click(100, 150);
        MarkItem placed = Assert.Single(e.Editor.State.Items.OfType<MarkItem>());
        Assert.Equal((preview.X, preview.Y, preview.Size), (placed.X, placed.Y, placed.Size));

        e.Editor.SelectToolCommand.Execute(EditorTool.Cross);
        e.Move(200, 150);
        Assert.Equal(MarkKind.Cross, e.Editor.MarkPreview?.Kind);

        // Leaving the page, or picking another tool, hides it.
        e.Editor.PointerExited();
        Assert.Null(e.Editor.MarkPreview);
        e.Move(200, 150);
        e.Editor.SelectToolCommand.Execute(EditorTool.Select);
        Assert.Null(e.Editor.MarkPreview);
    }

    [AvaloniaFact]
    public async Task Ink_starts_black_and_the_last_colour_chosen_is_remembered()
    {
        using (var first = await EditorSession.OpenAsync())
        {
            Assert.Equal(InkColor.Black, first.Editor.DefaultColour);
            first.Editor.SetColourCommand.Execute(InkColor.Red);
        }

        Assert.Equal("#b42318", Platform.AppSettings.Load().InkColour);

        // A new window (or a restart) starts with red. OpenAsync resets the setting, so open this one directly.
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
        EditorViewModel next = Editor(window);
        Assert.Equal(InkColor.Red, next.DefaultColour);
        Assert.True(next.IsRedChosen);
        window.CloseWithoutAsking();
        Platform.AppSettings.Update(settings => settings with { InkColour = null });
    }

    [AvaloniaFact]
    public async Task Single_letters_pick_tools_once_turned_on_in_settings()
    {
        using var e = await EditorSession.OpenAsync();

        // Off by default: a letter does nothing.
        e.Window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
        Assert.Equal(EditorTool.Select, e.Editor.Tool);
        Platform.AppSettings.Update(settings => settings with { UseSingleKeyTools = true });
        e.Window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
        Assert.Equal(EditorTool.Text, e.Editor.Tool);
        e.Window.KeyPress(Key.C, RawInputModifiers.None, PhysicalKey.C, "c");
        Assert.Equal(EditorTool.Tick, e.Editor.Tool);
        e.Window.KeyPress(Key.R, RawInputModifiers.None, PhysicalKey.R, "r");
        Assert.Equal(EditorTool.Shape, e.Editor.Tool);

        // With a modifier the letter is a command, not a tool: Mod+D duplicates rather than picking Date.
        e.Window.KeyPress(Key.D, s_command, PhysicalKey.D, "d");
        Assert.Equal(EditorTool.Shape, e.Editor.Tool);
        e.Window.KeyPress(Key.V, RawInputModifiers.None, PhysicalKey.V, "v");
        Assert.Equal(EditorTool.Select, e.Editor.Tool);

        // While typing on the page, letters are text.
        e.Editor.SelectToolCommand.Execute(EditorTool.Text);
        e.Click(120, 100);
        await Pump(() => e.TypingBox?.IsFocused == true);
        e.Window.KeyPress(Key.X, RawInputModifiers.None, PhysicalKey.X, "x");
        Assert.Equal(EditorTool.Text, e.Editor.Tool);
        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);

        try
        {
            Platform.AppSettings.Update(settings => settings with { UseSingleKeyTools = false });
            e.Window.KeyPress(Key.X, RawInputModifiers.None, PhysicalKey.X, "x");
            Assert.Equal(EditorTool.Text, e.Editor.Tool);
        }
        finally
        {
            Platform.AppSettings.Update(settings => settings with { UseSingleKeyTools = false });
        }
    }

    [AvaloniaFact]
    public async Task Bold_applies_to_typing_and_carry_on_to_new_text()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Text);
        Assert.True(e.Editor.OptionsHaveTextStyle);
        e.Click(120, 100);
        await Pump(() => e.TypingBox?.IsFocused == true);
        e.Window.KeyTextInput("Jordan Lee");
        e.Window.KeyPress(Key.B, s_command, PhysicalKey.B, null);
        e.Editor.SetColourCommand.Execute(InkColor.Red);
        await Pump(() => e.TypingBox?.FontWeight == FontWeight.Bold);
        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);

        TextItem typed = Assert.Single(e.Editor.State.Items.OfType<TextItem>());
        Assert.True(typed.IsBold);
        Assert.Equal(InkColor.Red, typed.Color);
        await e.SaveAsync("edit-bold");

        // The next text starts the same way; the buttons change a selected item too, as one undo step.
        e.Click(120, 300);
        await Pump(() => e.Editor.TypingItem is not null);
        TextItem next = (TextItem)e.Editor.State.FindItem(e.Editor.TypingItem!.Value)!;
        Assert.True(next.IsBold);
        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);

        e.Editor.SelectToolCommand.Execute(EditorTool.Select);
        e.Click(130, 100);
        Assert.Equal(ContextMode.Selection, e.Editor.ContextMode);
        e.Editor.ToggleBoldCommand.Execute(null);
        Assert.False(Assert.Single(e.Editor.State.Items.OfType<TextItem>()).IsBold);
        e.Editor.UndoCommand.Execute(null);
        Assert.True(Assert.Single(e.Editor.State.Items.OfType<TextItem>()).IsBold);
        Platform.AppSettings.Update(settings => settings with { InkColour = null });
    }

    [AvaloniaFact]
    public async Task The_pdfs_own_fields_are_filled_by_clicking_and_typing_and_undo_and_save_with_the_document()
    {
        using var e = await EditorSession.OpenAsync(file: "corpus/160F-2019.pdf");
        Pdf.Pdfium.FormField[] fields = [.. e.Editor.Source.FormFields(0)];
        var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
        PagePoint Centre(Pdf.Pdfium.FormField field) =>
            transform.ToDisplay((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Bottom + field.Rect.Top) / 2);

        // Typing into a text field: a box appears over it, Enter keeps the text as one undo step.
        PagePoint surname = Centre(fields.Single(field => field.Name == "A.NOM"));
        Assert.True(e.Editor.IsOverField(e.Editor.State.Pages[0].Id, surname));
        e.Click(surname.X, surname.Y);
        Assert.NotNull(e.Editor.EditingField);
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        TextBox box = e.Canvas.Children.OfType<TextBox>().Single(control => control.IsVisible);
        e.Window.KeyTextInput("Lee");
        e.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Pump(() => e.Editor.EditingField is null);
        Assert.Equal("Lee", e.Editor.State.Fields["A.NOM"]);
        Assert.False(box.IsVisible);

        // A radio button is chosen with a click; nothing is added as an item.
        Pdf.Pdfium.FormField non = fields.Single(field => field.Name == "R.CM" && field.OnValue == "Non");
        PagePoint radio = Centre(non);

        // It's below the window's visible part, so the click goes straight to the editor.
        e.Editor.PointerPressed(0, radio, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        e.Editor.PointerReleased(radio);
        Assert.Equal("Non", e.Editor.State.Fields["R.CM"]);
        Assert.Empty(e.Editor.State.Items);
        await Pump(() => e.Editor.Renderer.Has(new Rendering.RenderRequest(0, Rotation.None, e.Editor.Zoom)));
        await e.SaveAsync("edit-form-fields");

        e.Editor.UndoCommand.Execute(null);
        Assert.False(e.Editor.State.Fields.ContainsKey("R.CM"));
        e.Editor.RedoCommand.Execute(null);

        // Read-only fields (calculated totals) can't be filled.
        PagePoint total = Centre(fields.Single(field => field.Name == "T.TOTREM"));
        Assert.False(e.Editor.IsOverField(e.Editor.State.Pages[0].Id, total));
        Assert.True(e.Editor.IsOverReadOnlyField(e.Editor.State.Pages[0].Id, total));
        e.Editor.PointerPressed(0, total, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        e.Editor.PointerReleased(total);
        Assert.Contains("can’t be changed", e.Editor.Notice, StringComparison.Ordinal);
        Assert.False(e.Editor.State.Fields.ContainsKey("T.TOTREM"));

        // The final copy has the values, with the fields still there to change in another reader.
        string path = Path.Combine(Path.GetTempPath(), $"chainpen-fields-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Chainpen.Pdf.Export.DocumentWriter.FinalCopy(
                e.Editor.Source.Pdf.SaveCopy(removeSecurity: true),
                [.. Enumerable.Range(0, e.Editor.Source.PageCount).Select(e.Editor.Source.GetPage)],
                e.Editor.State).Pdf);
            using Pdf.Pdfium.PdfiumDocument final = Pdf.Pdfium.PdfiumDocument.Open(File.ReadAllBytes(path));
            Assert.Equal("Lee", final.GetFormFields(0).Single(field => field.Name == "A.NOM").Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Tab_past_the_last_field_goes_back_to_the_first_and_says_how_to_leave()
    {
        using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
        e.View.FindControl<ScrollViewer>("Scroller")!.Focus(NavigationMethod.Tab);
        e.Editor.MoveFieldFocus(backwards: true);
        Assert.Equal("Signature", e.Editor.FocusedField?.Field.Name);
        Assert.Contains("Tab to go back to the first field · F6 to leave the form", e.Editor.ContextHint, StringComparison.Ordinal);

        // Tab on the last field: back to the first, never an unseen stop (UX review 5).
        e.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await Pump(() => true);
        Assert.Equal("Name", e.Editor.EditingField?.Hit.Field.Name);

        // Shift+Tab in the first (a text field): round to the last.
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        e.Window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        await Pump(() => true);
        Assert.Equal("Signature", e.Editor.FocusedField?.Field.Name);
    }

    [AvaloniaFact]
    public async Task Once_focus_leaves_the_page_Space_and_arrows_no_longer_change_the_form_field_left_behind()
    {
        using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
        var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
        Pdf.Pdfium.FormField box = e.Editor.Source.FormFields(0).Single(field => field.Name == "ContactEmail");
        PagePoint at = transform.ToDisplay((box.Rect.Left + box.Rect.Right) / 2, (box.Rect.Bottom + box.Rect.Top) / 2);
        e.FocusPage();
        e.Editor.PointerPressed(0, at, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        e.Editor.PointerReleased(at);
        Assert.Equal("Yes", e.Editor.State.Fields["ContactEmail"]);
        Assert.Equal("ContactEmail", e.Editor.FocusedField?.Field.Name);

        // Shift+F6 goes to the page list. The check box keeps its outline, but the keys are the list's now.
        e.Key(Key.F6, RawInputModifiers.Shift);
        await Pump(() => true);
        Assert.False(e.View.IsPageFocused(e.Window.FocusManager?.GetFocusedElement()));
        e.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        e.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal("Yes", e.Editor.State.Fields["ContactEmail"]);

        // Back on the page, Space works on the field again.
        e.Key(Key.F6, RawInputModifiers.None);
        await Pump(() => e.View.IsPageFocused(e.Window.FocusManager?.GetFocusedElement()));
        e.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Assert.Equal(Pdf.Pdfium.PdfiumDocument.FieldOff, e.Editor.State.Fields["ContactEmail"]);
    }

    [AvaloniaFact]
    public async Task A_check_box_field_with_a_Yes_box_and_a_No_box_has_one_ticked_at_a_time()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chainpen-yes-no-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, YesNoForm());
        try
        {
            using var e = await EditorSession.OpenAsync(file: path);
            var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
            Pdf.Pdfium.FormField yes = e.Editor.Source.FormFields(0).Single(box => box.OnValue == "Yes");
            Pdf.Pdfium.FormField no = e.Editor.Source.FormFields(0).Single(box => box.OnValue == "No");
            void ClickBox(Pdf.Pdfium.FormField box)
            {
                PagePoint at = transform.ToDisplay((box.Rect.Left + box.Rect.Right) / 2, (box.Rect.Bottom + box.Rect.Top) / 2);
                e.Editor.PointerPressed(0, at, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
                e.Editor.PointerReleased(at);
            }

            ClickBox(yes);
            Assert.Equal("Yes", e.Editor.State.Fields["Citizen"]);
            Assert.True(e.Editor.IsTicked(yes));
            Assert.False(e.Editor.IsTicked(no));
            Assert.EndsWith("check box, ticked", e.Editor.FieldAnnouncement, StringComparison.Ordinal);

            // One click on the other box moves the tick, as in a PDF reader, and each box says its own state.
            ClickBox(no);
            Assert.Equal("No", e.Editor.State.Fields["Citizen"]);
            Assert.True(e.Editor.IsTicked(no));
            Assert.False(e.Editor.IsTicked(yes));
            Assert.EndsWith("check box, ticked", e.Editor.FieldAnnouncement, StringComparison.Ordinal);
            e.Editor.MoveFieldFocus(backwards: true);
            Assert.Equal("Yes", e.Editor.FocusedField?.Field.OnValue);
            Assert.EndsWith("check box, not ticked", e.Editor.FieldAnnouncement, StringComparison.Ordinal);

            ClickBox(no);
            Assert.Equal(Pdf.Pdfium.PdfiumDocument.FieldOff, e.Editor.State.Fields["Citizen"]);
            Assert.False(e.Editor.IsTicked(no));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A one-page PDF with a check box field, "Citizen", drawn as two boxes with the on-values Yes and No.</summary>
    private static byte[] YesNoForm()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 200] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R] >>",
            "<< /FT /Btn /T (Citizen) /V /Off /Kids [5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /P 3 0 R /Rect [20 150 40 170] /F 4 /AS /Off /AP << /N << /Yes 7 0 R /Off 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /P 3 0 R /Rect [60 150 80 170] /F 4 /AS /Off /AP << /N << /No 7 0 R /Off 8 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 14 >>\nstream\n0 0 20 20 re f\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = text.Length;
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>
    /// The ring round the form field in use stands out from the page (3:1) in the dark appearance too: the page stays
    /// light, so the dark theme's pale focus ring would all but vanish on it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ring_round_a_form_field_shows_on_the_page_in_dark_mode()
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.Dark });
        try
        {
            using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
            await Pump(() => e.Window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
            Pdf.Pdfium.FormField name = e.Editor.Source.FormFields(0).Single(field => field.Name == "Name");
            var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
            PagePoint corner = transform.ToDisplay(name.Rect.Left, name.Rect.Top);
            PagePoint opposite = transform.ToDisplay(name.Rect.Right, name.Rect.Bottom);
            e.Click((corner.X + opposite.X) / 2, (corner.Y + opposite.Y) / 2);
            await Pump(() => e.Editor.EditingField?.Hit.Field.Name == "Name");
            await e.SaveAsync("form-field-ring-dark");

            // The ring is drawn just outside the field's top edge: look a few pixels above it for its strongest colour.
            Point top = e.ToWindow((corner.X + opposite.X) / 2, Math.Min(corner.Y, opposite.Y));
            using WriteableBitmap frame = await e.FrameAsync();
            (byte, byte, byte) page = Pixel(frame, (int)top.X, (int)top.Y - 12);
            double strongest = Enumerable.Range(1, 8).Max(above => Contrast(Pixel(frame, (int)top.X, (int)top.Y - above), page));
            Assert.True(strongest >= 3, $"the ring is {strongest:0.00}:1 on the page");
        }
        finally
        {
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }
    }

    /// <summary>
    /// Text selected while typing on the page, or in one of the PDF's own fields, stays readable (4.5:1) in the dark
    /// appearance: the page is light there, so the dark theme's pale text colour can't be used for it.
    /// </summary>
    [AvaloniaFact]
    public async Task Selected_text_on_the_page_stays_readable_in_dark_mode()
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.Dark });
        try
        {
            using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
            await Pump(() => e.Window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

            Pdf.Pdfium.FormField name = e.Editor.Source.FormFields(0).Single(field => field.Name == "Name");
            var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
            PagePoint corner = transform.ToDisplay(name.Rect.Left, name.Rect.Top);
            PagePoint opposite = transform.ToDisplay(name.Rect.Right, name.Rect.Bottom);
            e.Click((corner.X + opposite.X) / 2, (corner.Y + opposite.Y) / 2);
            await Pump(() => e.TypingBox?.IsFocused == true);
            await AssertSelectionReadable(e, "WWWWWWWW", "form-field-selection-dark");
            e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Pump(() => e.Editor.EditingField is null);

            e.Editor.SelectToolCommand.Execute(EditorTool.Text);
            e.Click(80, 40);
            await Pump(() => e.TypingBox?.IsFocused == true);
            await AssertSelectionReadable(e, "WWWWWWWW", "page-text-selection-dark");
        }
        finally
        {
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }

        static async Task AssertSelectionReadable(EditorSession e, string text, string screenshot)
        {
            TextBox box = e.TypingBox!;
            e.Window.KeyTextInput(text);
            await Pump(() => box.Text == text);
            box.SelectAll();
            await e.SaveAsync(screenshot);

            // The selection is the commonest colour across the text; the letters are what differs most from it.
            Point topLeft = box.TranslatePoint(default, e.Window)!.Value;
            using WriteableBitmap frame = await e.FrameAsync();
            var colours = new List<(byte, byte, byte)>();
            for (int x = (int)topLeft.X + 2; x < (int)(topLeft.X + box.Bounds.Width) - 2; x++)
            {
                for (int y = (int)topLeft.Y + 2; y < (int)(topLeft.Y + box.Bounds.Height) - 2; y++)
                {
                    colours.Add(Pixel(frame, x, y));
                }
            }

            (byte, byte, byte) selection = colours.GroupBy(colour => colour).OrderByDescending(group => group.Count()).First().Key;
            double letters = colours.Max(colour => Contrast(colour, selection));
            Assert.True(letters >= 4.5, $"{screenshot}: selected text is {letters:0.00}:1 on {selection}");
        }
    }

    [AvaloniaFact]
    public async Task Tab_in_a_form_field_keeps_it_and_moves_to_the_next_field()
    {
        using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
        Pdf.Pdfium.FormField[] fields = [.. e.Editor.Source.FormFields(0)];
        var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
        Pdf.Pdfium.FormField name = fields.Single(field => field.Name == "Name");
        PagePoint at = transform.ToDisplay((name.Rect.Left + name.Rect.Right) / 2, (name.Rect.Bottom + name.Rect.Top) / 2);
        e.Click(at.X, at.Y);
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        e.Window.KeyTextInput("Jordan");
        TextBox first = e.Canvas.Children.OfType<TextBox>().Single(control => control.IsVisible && control.IsFocused);
        e.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await Pump(() => e.Editor.State.Fields.ContainsKey("Name"));

        // Focus really moves to a box named for the next field, so screen readers announce it.
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused && !ReferenceEquals(control, first)));
        TextBox emailBox = e.Canvas.Children.OfType<TextBox>().Single(control => control.IsVisible && control.IsFocused);
        Assert.Equal("Your email address, text field", Avalonia.Automation.AutomationProperties.GetName(emailBox));
        Assert.Equal("Jordan", e.Editor.State.Fields["Name"]);
        Assert.Equal("Email", e.Editor.EditingField?.Hit.Field.Name);
        Assert.True(e.Editor.EditingField?.SelectsAll, "Tab selects the next field's text, as in a browser");

        // Tab carries on past the text fields to the check boxes, which Space ticks.
        foreach (string next in (string[])["DateOfBirth", "Address"])
        {
            await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
            e.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            await Pump(() => e.Editor.EditingField?.Hit.Field.Name == next);
        }

        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        e.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await Pump(() => e.Editor.FocusedField?.Field.Name == "ContactEmail");
        Assert.Null(e.Editor.EditingField);
        Assert.Equal("Contact me by: Email, check box, not ticked", e.Editor.FieldAnnouncement);
        e.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Assert.Equal("Yes", e.Editor.State.Fields["ContactEmail"]);
        Assert.Equal("Contact me by: Email, check box, ticked", e.Editor.FieldAnnouncement);
        e.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.Equal("ContactPhone", e.Editor.FocusedField?.Field.Name);

        // As in a browser, the arrow keys don't move between check boxes (each is its own Tab stop).
        e.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal("ContactPhone", e.Editor.FocusedField?.Field.Name);
        Assert.False(e.Editor.State.Fields.ContainsKey("ContactPhone"));
        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(e.Editor.FocusedField);

        // A list field picks the row clicked; a drop-down offers a menu instead.
        Pdf.Pdfium.FormField heard = fields.Single(field => field.Name == "Heard");
        Assert.True(heard.IsList);
        Assert.False(fields.Single(field => field.Name == "State").IsList);
        PagePoint top = transform.ToDisplay(heard.Rect.Left + 20, heard.Rect.Top);
        var second = new PagePoint(top.X, top.Y + 1 + (heard.ListRowHeight * 1.5));
        e.Editor.PointerPressed(0, second, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        e.Editor.PointerReleased(second);
        Assert.Equal("Search", e.Editor.State.Fields["Heard"]);

        // It's a multi-select list: another row is added, and a chosen row clicked again is taken off.
        var fourth = new PagePoint(top.X, top.Y + 1 + (heard.ListRowHeight * 3.5));
        foreach (PagePoint row in (PagePoint[])[fourth, second])
        {
            e.Editor.PointerPressed(0, row, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            e.Editor.PointerReleased(row);
        }

        Assert.Equal("Newspaper", e.Editor.State.Fields["Heard"]);

        // Arrow keys inside the field Tab reached: radio buttons, a drop-down, and a multi-select list's rows.
        e.FocusPage();
        void Press(Key key, PhysicalKey physical) => e.Window.KeyPress(key, RawInputModifiers.None, physical, null);

        // Tab from a field: wait for a text field's box to take focus first, as a person would.
        async Task TabAsync(bool backwards)
        {
            if (e.Editor.EditingField is not null)
            {
                await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
            }

            e.Window.KeyPress(Key.Tab, backwards ? RawInputModifiers.Shift : RawInputModifiers.None, PhysicalKey.Tab, null);
            await Pump(() => true);
        }

        string? Current() => e.Editor.FocusedField?.Field.Name ?? e.Editor.EditingField?.Hit.Field.Name;

        // The list click left the focus on "How did you hear?". Shift+Tab goes back through Postcode and State to the
        // radio group: one stop, on its first button since none is chosen; the arrows wrap round the group.
        foreach (string previous in (string[])["Postcode", "State", "Membership"])
        {
            await TabAsync(backwards: true);
            Assert.Equal(previous, Current());
        }

        Assert.Equal("Individual", e.Editor.FocusedField?.Field.OnValue);
        Press(Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal("Business", e.Editor.State.Fields["Membership"]);
        Assert.Equal("Membership, radio button Business, 3 of 3, chosen", e.Editor.FieldAnnouncement);
        Press(Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal("Individual", e.Editor.State.Fields["Membership"]);
        Press(Key.Tab, PhysicalKey.Tab);
        Assert.Equal("State", e.Editor.FocusedField?.Field.Name);
        Press(Key.Down, PhysicalKey.ArrowDown);
        Press(Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("NSW", e.Editor.State.Fields["State"]);

        // Multi-select list keys (WAI-ARIA list box pattern): arrows move the outline without changing what's
        // chosen, Space ticks or unticks, Shift+arrows choose the rows from where the outline was.
        foreach (string next in (string[])["Postcode", "Heard"])
        {
            await TabAsync(backwards: false);
            Assert.Equal(next, Current());
        }

        Assert.Equal(3, e.Editor.FocusedOption); // starts on the chosen row
        Press(Key.Up, PhysicalKey.ArrowUp);
        Press(Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(1, e.Editor.FocusedOption);
        Assert.Equal("Newspaper", e.Editor.State.Fields["Heard"]);
        Press(Key.Space, PhysicalKey.Space);
        Assert.Equal("Search\nNewspaper", e.Editor.State.Fields["Heard"]);
        Assert.Equal("How did you hear?, list, several answers, Search, ticked, 2 of 4 chosen", e.Editor.FieldAnnouncement);
        Press(Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("Search\nNewspaper", e.Editor.State.Fields["Heard"]);
        Press(Key.Up, PhysicalKey.ArrowUp);
        e.Window.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        e.Window.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        Assert.Equal("Search\nSocial media\nNewspaper", e.Editor.State.Fields["Heard"]);
    }

    [AvaloniaFact]
    public async Task Signature_fields_buttons_limits_typed_answers_and_gaps_for_form_fields()
    {
        using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
        PageId page = e.Editor.State.Pages[0].Id;
        Pdf.Pdfium.FormField[] fields = [.. e.Editor.Source.FormFields(0)];
        var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
        PagePoint Centre(string name)
        {
            Pdf.Pdfium.FormField field = fields.First(field => field.Name == name);
            return transform.ToDisplay((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Bottom + field.Rect.Top) / 2);
        }

        void Press(PagePoint at)
        {
            e.Editor.PointerPressed(0, at, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            e.Editor.PointerReleased(at);
        }

        // Gaps: nothing filled yet, so nothing is checked; once a field is filled, the page's empty fields are hints.
        Assert.Equal(0, e.Editor.FindGaps().PagesChecked);
        e.Editor.SetField("Name", "Jordan Lee");
        Core.Gaps.GapReport gaps = e.Editor.FindGaps();
        Assert.Equal(1, gaps.PagesChecked);
        string[] labels = [.. gaps.Gaps.Select(gap => gap.Label)];
        Assert.Contains("Your email address", labels);
        Assert.Contains("Your signature", labels);
        Assert.Contains(gaps.Gaps, gap => gap.Kind == Core.Gaps.GapKind.Unticked && gap.Label.Contains("Individual", StringComparison.Ordinal));
        Assert.DoesNotContain("Name", labels);
        Assert.DoesNotContain("Total", labels);
        Assert.DoesNotContain("Reset", labels);

        // A push button says why nothing happens.
        Assert.True(e.Editor.IsOverReadOnlyField(page, Centre("Reset")));
        Press(Centre("Reset"));
        Assert.Contains("doesn’t run form buttons", e.Editor.Notice, StringComparison.Ordinal);

        // A text field with a character limit stops there.
        Press(Centre("Postcode"));
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        TextBox postcode = e.Canvas.Children.OfType<TextBox>().Single(control => control.IsVisible);
        Assert.Equal(4, postcode.MaxLength);
        Assert.Equal("Postcode, text field, up to 4 characters", Avalonia.Automation.AutomationProperties.GetName(postcode));

        // It's a comb field: typing shows its boxes, a character in each.
        Assert.True(e.Editor.EditingField!.Hit.Field.IsComb);
        e.Window.KeyTextInput("470123");
        await Pump(() => true);
        Assert.Equal("4701", e.Canvas.Children.OfType<TextBox>().Single(control => control.IsVisible).Text);
        Save(e.Window, "edit-form-comb-typing");
        e.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Pump(() => e.Editor.EditingField is null);
        Assert.Equal("4701", e.Editor.State.Fields["Postcode"]);

        // A drop-down that takes typing.
        FieldHit title = e.Editor.FieldAt(page, Centre("Title"))!;
        e.Editor.TypeIntoField(title);
        await Pump(() => e.Canvas.Children.OfType<TextBox>().Any(control => control.IsVisible && control.IsFocused));
        e.Window.KeyTextInput("Prof");
        e.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Pump(() => e.Editor.EditingField is null);
        Assert.Equal("Prof", e.Editor.State.Fields["Title"]);

        // A signature field: sign, and the signature lands inside its box; that gap is then filled.
        Assert.Single(e.Editor.UnsignedSignatureFields(page));
        Press(Centre("Signature"));
        SignatureDialog dialog = await Await(() => e.Window.FindControl<ContentControl>("DialogHost")!.Content as SignatureDialog);
        var signing = (SignatureDialogViewModel)dialog.DataContext!;
        signing.IsTypeTab = true;
        signing.TypedName = "Jordan Lee";
        signing.PlaceCommand.Execute(null);
        await Pump(() => e.Editor.State.Items.OfType<SignatureItem>().Any());
        SignatureItem signature = e.Editor.State.Items.OfType<SignatureItem>().Single();
        PageRect box = e.Editor.FieldAt(page, Centre("Signature"))!.Box;
        Assert.True(signature.X >= box.X && signature.X + signature.Width <= box.Right + 0.01, "fits across");
        Assert.True(signature.Y >= box.Y && signature.Y + signature.Height <= box.Bottom + 0.01, "fits down");
        Assert.DoesNotContain("Your signature", e.Editor.FindGaps().Gaps.Select(gap => gap.Label));
        Assert.Empty(e.Editor.UnsignedSignatureFields(page));
        await e.SaveAsync("edit-form-signature");
    }

    [AvaloniaFact]
    public async Task On_a_form_the_tools_and_hints_point_at_its_fields_and_screen_readers_can_reach_them()
    {
        using var e = await EditorSession.OpenAsync(file: "form-fields.pdf");
        PageId page = e.Editor.State.Pages[0].Id;
        var transform = new Core.Geometry.PageTransform(e.Editor.Source.GetPage(0), Rotation.None);
        Pdf.Pdfium.FormField phone = e.Editor.Source.FormFields(0).Single(field => field.Name == "ContactPhone");
        PagePoint phoneCentre = transform.ToDisplay((phone.Rect.Left + phone.Rect.Right) / 2, (phone.Rect.Bottom + phone.Rect.Top) / 2);

        // Opening says how many fields there are in the bar, with nothing over the page; filling one shows progress.
        await Pump(() => e.Editor.ContextHint.StartsWith("14 fields to fill", StringComparison.Ordinal));
        Assert.Null(e.Editor.Notice);
        e.Editor.SetField("Name", "Jordan");
        Assert.Equal("1 of 14 filled · Tab for the next field · Export when you’re done", e.Editor.ContextHint);
        e.Editor.SetField("Name", string.Empty);

        // Tick on one of the form's boxes ticks the box itself; no separate mark is added.
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Editor.PointerMoved(0, phoneCentre);
        Assert.Null(e.Editor.MarkPreview);
        e.Editor.PointerPressed(0, phoneCentre, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        e.Editor.PointerReleased(phoneCentre);
        Assert.Equal("Yes", e.Editor.State.Fields["ContactPhone"]);
        Assert.Empty(e.Editor.State.Items);
        e.Editor.SelectToolCommand.Execute(EditorTool.Select);

        // Gaps are worded as fields and questions.
        string[] gaps = [.. e.Editor.FindGaps().Gaps.Select(EditorViewModel.DescribeForExport)];
        Assert.Contains("“Your email address” is empty on page 1", gaps);
        Assert.Contains("“Membership” isn’t answered on page 1", gaps);
        Assert.Contains("“Your signature” isn’t signed on page 1", gaps);

        // The bar shows the field in use and its keys.
        e.Editor.FocusField(e.Editor.FieldAt(page, phoneCentre)!);
        Assert.Equal("“Phone” · Space to tick or untick · Tab for the next field", e.Editor.ContextHint);

        // Screen readers see each field as its control, with its value, and can act on it.
        var canvasPeer = (Avalonia.Automation.Peers.ControlAutomationPeer)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(e.Canvas);
        var fields = canvasPeer.GetChildren().OfType<FieldAutomationPeer>().ToList();
        Assert.Contains(fields, peer => peer.GetName() == "Full name" && peer.GetAutomationControlType() == Avalonia.Automation.Peers.AutomationControlType.Edit);
        var phonePeer = fields.Single(peer => peer.GetAutomationId() == "ContactPhone");
        var toggle = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IToggleProvider>(phonePeer);
        Assert.Equal(Avalonia.Automation.Provider.ToggleState.On, toggle.ToggleState);
        toggle.Toggle();
        Assert.Equal("Off", e.Editor.State.Fields["ContactPhone"]);
        var name = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IValueProvider>(fields.Single(peer => peer.GetAutomationId() == "Name"));
        name.SetValue("Jordan Lee");
        Assert.Equal("Jordan Lee", e.Editor.State.Fields["Name"]);
        Assert.Contains(fields, peer => peer.GetName() == "Membership: Family" && peer.GetAutomationControlType() == Avalonia.Automation.Peers.AutomationControlType.RadioButton);
    }

    [AvaloniaFact]
    public async Task The_pages_panel_resizes_from_its_edge_is_remembered_and_Reset_layout_restores_it()
    {
        Platform.AppSettings.Update(settings => settings with { PagesPanelWidth = Platform.AppSettings.DefaultPagesPanelWidth });
        using var e = await EditorSession.OpenAsync();
        Border panel = e.View.FindControl<Border>("PagesPanel")!;
        Border edge = e.View.FindControl<Border>("PagesPanelResizer")!;
        Assert.Equal(196, panel.Width);

        // Keyboard: focus the edge, then the arrow keys; thumbnails follow the width, and it's remembered.
        edge.Focus();
        e.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        e.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(228, panel.Width);
        Assert.Equal(148.0, e.View.Resources["PagesThumbWidth"]);
        Assert.Equal(228, Platform.AppSettings.Load().PagesPanelWidth);

        // It stops at its limits.
        for (int i = 0; i < 30; i++)
        {
            e.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        }

        Assert.Equal(Platform.AppSettings.MinPagesPanelWidth, panel.Width);

        // View → Reset layout: standard width, and the panel shown again.
        e.Editor.IsPagesPanelVisible = false;
        e.View.ResetLayout();
        Assert.Equal(196, panel.Width);
        Assert.True(e.Editor.IsPagesPanelVisible);
        Assert.Equal(196, Platform.AppSettings.Load().PagesPanelWidth);
    }

    [AvaloniaFact]
    public async Task The_next_window_opens_at_the_size_the_last_one_closed_at()
    {
        var first = new MainWindow(isFirstLaunch: false, []);
        first.Show();
        first.Width = 1100;
        first.Height = 780;
        await Pump(() => true);
        first.CloseWithoutAsking();
        await Pump(() => true);
        Assert.Equal((1100.0, 780.0), (Platform.AppSettings.Load().Window!.Width, Platform.AppSettings.Load().Window!.Height));

        var next = new MainWindow(isFirstLaunch: false, []);
        Assert.Equal((1100.0, 780.0), (next.Width, next.Height));
        next.ResetWindowSize();
        Assert.Equal((MainWindow.DefaultWidth, MainWindow.DefaultHeight), (next.Width, next.Height));
        Platform.AppSettings.Update(settings => settings with { Window = null });
    }

    private static async Task<T> Await<T>(Func<T?> find)
        where T : class
    {
        T? found = null;
        await Pump(() => (found = find()) is not null);
        return found!;
    }

    [AvaloniaFact]
    public async Task Tick_is_centred_on_the_click_and_the_tool_stays_on()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(100, 150);
        e.Click(100, 200);

        var ticks = e.Editor.State.Items.OfType<MarkItem>().ToList();
        Assert.Equal(2, ticks.Count);
        Assert.All(ticks, tick => Assert.Equal(MarkKind.Tick, tick.Kind));
        Assert.Equal(100, ticks[0].X + (ticks[0].Size / 2), 0.5);
        Assert.Equal(150, ticks[0].Y + (ticks[0].Size / 2), 0.5);
        Assert.Equal(InkColor.Black, ticks[0].Color);
        Assert.Equal(EditorTool.Tick, e.Editor.Tool);
        Assert.True(e.Editor.Pages[0].HasEdits);
        Assert.Equal(ContextMode.ToolOptions, e.Editor.ContextMode);
        await e.SaveAsync("edit-tick-tool");

        e.Editor.UndoCommand.Execute(null);
        Assert.Single(e.Editor.State.Items);
    }

    [AvaloniaFact]
    public async Task Text_is_typed_in_place_finishes_with_Esc_and_empty_text_is_discarded()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Text);
        e.Click(120, 100);
        Assert.NotNull(e.Editor.TypingItem);
        await Pump(() => e.TypingBox?.IsFocused == true);
        e.Window.KeyTextInput("Jordan Lee");
        await Pump(() => e.Editor.State.Items.OfType<TextItem>().SingleOrDefault()?.Text == "Jordan Lee");
        Assert.Equal(ContextMode.Typing, e.Editor.ContextMode);
        await e.SaveAsync("edit-typing");

        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);
        TextItem text = Assert.Single(e.Editor.State.Items.OfType<TextItem>());
        Assert.Equal("Jordan Lee", text.Text);

        // Typing is one undo step.
        e.Editor.UndoCommand.Execute(null);
        Assert.Empty(e.Editor.State.Items);
        e.Editor.RedoCommand.Execute(null);
        Assert.Single(e.Editor.State.Items);

        // Starting a text box and leaving it empty adds nothing.
        e.Click(120, 300);
        Assert.NotNull(e.Editor.TypingItem);
        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);
        Assert.Single(e.Editor.State.Items);
        e.Editor.UndoCommand.Execute(null);
        Assert.Empty(e.Editor.State.Items);
    }

    [AvaloniaFact]
    public async Task Text_being_typed_sits_exactly_where_the_page_draws_it()
    {
        using var e = await EditorSession.OpenAsync();

        // The ink is found by its blue, so type in blue.
        e.Editor.SetColourCommand.Execute(InkColor.Blue);
        e.Editor.SelectToolCommand.Execute(EditorTool.Text);
        e.Click(150, 120);
        await Pump(() => e.TypingBox?.IsFocused == true);
        e.Window.KeyTextInput("Jordan Lee 17/11");
        await Pump(() => e.Editor.State.Items.OfType<TextItem>().SingleOrDefault()?.Text == "Jordan Lee 17/11");
        TextItem text = e.Editor.State.Items.OfType<TextItem>().Single();
        PixelRect region = e.ItemRegion(text);
        using WriteableBitmap typingFrame = await e.FrameAsync();
        var whileTyping = InkBounds(typingFrame, region);

        e.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => e.Editor.TypingItem is null);
        using WriteableBitmap drawnFrame = await e.FrameAsync();
        var drawn = InkBounds(drawnFrame, region);

        output.WriteLine($"typing {whileTyping}, drawn {drawn}");
        Assert.InRange(whileTyping.Left - drawn.Left, -1, 1);
        Assert.InRange(whileTyping.Right - drawn.Right, -1, 1);
        Assert.InRange(whileTyping.Top - drawn.Top, -1, 1);
        Assert.InRange(whileTyping.Bottom - drawn.Bottom, -1, 1);
    }

    [AvaloniaFact]
    public async Task Placing_a_date_returns_to_Select_with_the_date_selected_and_double_click_edits_it()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Date);
        e.Click(200, 150);
        TextItem date = Assert.Single(e.Editor.State.Items.OfType<TextItem>());
        Assert.Equal(Platform.DateFormats.Format(DateTime.Today), date.Text);
        Assert.Null(e.Editor.TypingItem);
        Assert.Equal(EditorTool.Select, e.Editor.Tool);
        Assert.Equal([date.Id], e.Editor.SelectedIds);

        double x = date.X + 5, y = date.Y + 5;
        e.Click(x, y);
        e.Click(x, y, clickCount: 2);
        Assert.Equal(date.Id, e.Editor.TypingItem);
    }

    [AvaloniaFact]
    public async Task Shapes_are_dragged_or_clicked_and_then_selected()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Rectangle);
        Assert.Equal("Rectangle", e.Editor.ToolLabel);
        e.Drag((300, 100), (400, 160));

        ShapeItem rectangle = Assert.Single(e.Editor.State.Items.OfType<ShapeItem>());
        Assert.Equal(ShapeKind.Rectangle, rectangle.Kind);
        Assert.Equal(300, rectangle.X, 0.5);
        Assert.Equal(100, rectangle.Width, 0.5);
        Assert.Equal(60, rectangle.Height, 0.5);
        Assert.Equal(EditorTool.Select, e.Editor.Tool);
        Assert.Equal([rectangle.Id], e.Editor.SelectedIds);
        Assert.Equal("Rectangle selected", e.Editor.SelectionLabel);

        // A click instead of a drag gives the standard size.
        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Ellipse);
        e.Click(300, 200);
        ShapeItem ellipse = e.Editor.State.Items.OfType<ShapeItem>().Last();
        Assert.Equal((80, 40), (ellipse.Width, ellipse.Height));

        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Highlight);
        Assert.False(e.Editor.OptionsHaveColour);
        e.Drag((100, 260), (250, 274));
        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Arrow);
        e.Drag((420, 300), (330, 250));
        ShapeItem arrow = e.Editor.State.Items.OfType<ShapeItem>().Last();
        Assert.True(arrow.FlipX);
        Assert.True(arrow.FlipY);
        await e.SaveAsync("edit-shapes");
    }

    [AvaloniaFact]
    public async Task Things_inside_an_outlined_shape_can_still_be_selected_and_empty_space_inside_moves_it()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Rectangle);
        e.Drag((300, 100), (400, 160));
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(350, 130);
        MarkItem tick = Assert.Single(e.Editor.State.Items.OfType<MarkItem>());

        e.Editor.SelectToolCommand.Execute(EditorTool.Select);
        e.Click(350, 130);
        Assert.Equal([tick.Id], e.Editor.SelectedIds);

        // Inside the rectangle but away from the tick grabs the rectangle, as the outline does.
        e.Click(320, 150);
        ShapeItem rectangle = Assert.IsType<ShapeItem>(Assert.Single(e.Editor.SelectedItems()));
        e.Click(150, 300);
        Assert.Empty(e.Editor.SelectedIds);
        e.Click(300, 140);
        Assert.Equal([rectangle.Id], e.Editor.SelectedIds);

        // Dragging from empty space inside moves it.
        e.Drag((320, 150), (340, 170));
        ShapeItem moved = Assert.IsType<ShapeItem>(e.Editor.State.FindItem(rectangle.Id));
        Assert.Equal(rectangle.X + 20, moved.X, 3);
        Assert.Equal(rectangle.Y + 20, moved.Y, 3);
    }

    [AvaloniaFact]
    public async Task Scenario_7_box_select_three_ticks_move_copy_paste_delete_undo_redo()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(100, 150);
        e.Click(100, 180);
        e.Click(100, 210);
        e.Editor.SelectToolCommand.Execute(EditorTool.Select);

        // Drag a box around them; the bar counts them before letting go (E5).
        e.Down(80, 135);
        e.Move(100, 180);
        e.Move(122, 226);
        Assert.Equal(3, e.Editor.BoxCandidateCount);
        Assert.Equal(ContextMode.BoxSelecting, e.Editor.ContextMode);
        await e.SaveAsync("edit-box-select");
        e.Up(122, 226);
        Assert.Equal(3, e.Editor.SelectedIds.Count);
        Assert.Equal("3 ticks", e.Editor.SelectionLabel);

        // Dragging one moves all three together.
        var before = e.Editor.State.Items.ToDictionary(item => item.Id, item => item.X);
        e.Drag((100, 180), (150, 180));
        Assert.All(e.Editor.State.Items, item => Assert.Equal(before[item.Id] + 50, item.X, 0.5));
        e.Editor.UndoCommand.Execute(null);
        Assert.All(e.Editor.State.Items, item => Assert.Equal(before[item.Id], item.X, 0.5));
        e.Editor.RedoCommand.Execute(null);

        // Copy and paste: three offset copies, selected.
        e.Key(Key.C, s_command);
        e.Key(Key.V, s_command);
        Assert.Equal(6, e.Editor.State.Items.Count);
        Assert.Equal(3, e.Editor.SelectedIds.Count);
        var copies = e.Editor.SelectedItems().ToList();
        Assert.All(copies, copy => Assert.Equal(before.Values.First() + 50 + 12, copy.X, 0.5));
        await e.SaveAsync("edit-pasted");

        e.Key(Key.Delete, RawInputModifiers.None);
        Assert.Equal(3, e.Editor.State.Items.Count);
        Assert.True(e.Editor.HasToast);
        Assert.Equal("3 items deleted", e.Editor.Notice);
        e.Editor.UndoCommand.Execute(null);
        Assert.Equal(6, e.Editor.State.Items.Count);
        e.Editor.RedoCommand.Execute(null);
        Assert.Equal(3, e.Editor.State.Items.Count);
    }

    [AvaloniaFact]
    public async Task Pen_strokes_join_into_one_drawing_until_the_colour_changes()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Draw);
        Assert.Equal(ContextMode.Drawing, e.Editor.ContextMode);
        e.Drag((150, 300), (200, 330), (260, 300));
        e.Drag((160, 340), (250, 340));
        InkItem drawing = Assert.Single(e.Editor.State.Items.OfType<InkItem>());
        Assert.Equal(2, drawing.Strokes.Length);
        await e.SaveAsync("edit-drawing");

        e.Editor.SetColourCommand.Execute(InkColor.Red);
        e.Drag((300, 300), (340, 340));
        Assert.Equal(2, e.Editor.State.Items.OfType<InkItem>().Count());
        Assert.Equal(InkColor.Red, e.Editor.State.Items.OfType<InkItem>().Last().Color);

        // Drawing over a tick doesn't select or move it (FR-3.8).
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(400, 320);
        Item tick = e.Editor.State.Items.Last();
        e.Editor.SelectToolCommand.Execute(EditorTool.Draw);
        e.Drag((390, 310), (420, 330));
        Assert.Equal(tick, e.Editor.State.FindItem(tick.Id));
        Assert.Empty(e.Editor.SelectedIds);

        // Done (Esc) selects the drawing just made.
        e.Key(Key.Escape, RawInputModifiers.None);
        Assert.Equal(EditorTool.Select, e.Editor.Tool);
        Assert.IsType<InkItem>(Assert.Single(e.Editor.SelectedItems()));
    }

    [AvaloniaFact]
    public async Task A_corner_handle_resizes_and_arrow_keys_nudge()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.ChooseShapeCommand.Execute(ShapeKind.Rectangle);
        e.Drag((300, 100), (400, 160));
        ShapeItem shape = e.Editor.State.Items.OfType<ShapeItem>().Single();

        // The bottom-right handle sits just outside the corner.
        Point corner = e.ToWindow(400, 160) + new Point(3, 3);
        double zoom = e.Editor.Zoom;
        e.Window.MouseDown(corner, MouseButton.Left, RawInputModifiers.None);
        e.Window.MouseMove(corner + new Point(20 * zoom, 10 * zoom), RawInputModifiers.None);
        e.Window.MouseUp(corner + new Point(20 * zoom, 10 * zoom), MouseButton.Left, RawInputModifiers.None);
        ShapeItem resized = e.Editor.State.Items.OfType<ShapeItem>().Single();
        Assert.Equal(shape.X, resized.X, 0.5);
        Assert.Equal(120, resized.Width, 0.5);
        Assert.Equal(70, resized.Height, 0.5);
        await e.SaveAsync("edit-one-selected");

        e.FocusPage();
        e.Key(Key.Right, RawInputModifiers.None);
        e.Key(Key.Down, RawInputModifiers.Shift);
        ShapeItem nudged = e.Editor.State.Items.OfType<ShapeItem>().Single();
        Assert.Equal(resized.X + 1, nudged.X, 3);
        Assert.Equal(resized.Y + 10, nudged.Y, 3);
    }

    [AvaloniaFact]
    public async Task A_signature_follows_the_pointer_and_can_be_placed_with_the_keyboard()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.BeginPlacingSignature(TestSignature(), SignatureKind.Signature);
        Assert.Equal(ContextMode.Placing, e.Editor.ContextMode);
        e.Window.MouseMove(e.ToWindow(250, 300), RawInputModifiers.None);
        await e.SaveAsync("edit-placing-signature");

        // Arrow keys move it from where the pointer left it; Enter places it (FS-311, NFR-4).
        e.FocusPage();
        e.Key(Key.Right, RawInputModifiers.None);
        e.Key(Key.Right, RawInputModifiers.Shift);
        e.Key(Key.Enter, RawInputModifiers.None);
        SignatureItem signature = Assert.Single(e.Editor.State.Items.OfType<SignatureItem>());
        Assert.Equal(250 + EditorViewModel.CursorStep + EditorViewModel.CursorLargeStep, signature.X + (signature.Width / 2), 0.5);
        Assert.Equal(EditorViewModel.SignatureWidth, signature.Width, 3);
        Assert.Equal([signature.Id], e.Editor.SelectedIds);
        Assert.Equal("Signature selected", e.Editor.SelectionLabel);
        Assert.EndsWith("contains your signature", e.Editor.DocumentStatus, StringComparison.Ordinal);
        await e.SaveAsync("edit-signature-selected");

        // Esc while placing cancels.
        e.Editor.BeginPlacingSignature(TestSignature(), SignatureKind.Initials);
        e.Key(Key.Escape, RawInputModifiers.None);
        Assert.Null(e.Editor.PlacingSignature);
        Assert.Single(e.Editor.State.Items);
    }

    [AvaloniaFact]
    public async Task The_next_signature_and_initials_are_placed_at_the_size_last_resized_to()
    {
        using var e = await EditorSession.OpenAsync();
        double zoom = e.Editor.Zoom;
        foreach ((SignatureKind kind, double startWidth, double x) in new[]
        {
            (SignatureKind.Signature, EditorViewModel.SignatureWidth, 200.0),
            (SignatureKind.Initials, EditorViewModel.InitialsWidth, 420.0),
        })
        {
            e.Editor.BeginPlacingSignature(TestSignature(), kind);
            e.Click(x, 200);
            SignatureItem placed = e.Editor.State.Items.OfType<SignatureItem>().Last();
            Assert.Equal(startWidth, placed.Width, 3);

            // Drag the bottom-right handle 30 pt wider.
            Point corner = e.ToWindow(placed.X + placed.Width, placed.Y + placed.Height) + new Point(3, 3);
            e.Window.MouseDown(corner, MouseButton.Left, RawInputModifiers.None);
            e.Window.MouseMove(corner + new Point(30 * zoom, 30 * zoom), RawInputModifiers.None);
            e.Window.MouseUp(corner + new Point(30 * zoom, 30 * zoom), MouseButton.Left, RawInputModifiers.None);
            double resizedWidth = e.Editor.State.FindItem(placed.Id) is SignatureItem resized ? resized.Width : 0;
            Assert.True(resizedWidth > startWidth + 1, $"{kind} was not resized");

            e.Editor.BeginPlacingSignature(TestSignature(), kind);
            e.Click(x, 500);
            SignatureItem next = e.Editor.State.Items.OfType<SignatureItem>().Last();
            Assert.Equal(resizedWidth, next.Width, 3);
        }

        // Each kind keeps its own size.
        Assert.NotEqual(e.Editor.DefaultSignatureWidth, e.Editor.DefaultInitialsWidth);
    }

    [AvaloniaFact]
    public async Task The_next_date_is_placed_at_the_size_the_last_one_was_resized_to()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Date);
        e.Click(200, 150);
        TextItem date = Assert.Single(e.Editor.State.Items.OfType<TextItem>());
        Assert.Equal(TextItem.DefaultFontSize, date.FontSize, 3);

        // Placing a date leaves it selected, so its bottom-right handle can be dragged straight away.
        PageRect bounds = Chainpen.Core.Geometry.ItemGeometry.Bounds(date, e.Editor.Measurer);
        Point corner = e.ToWindow(bounds.Right, bounds.Bottom) + new Point(3, 3);
        double zoom = e.Editor.Zoom;
        e.Window.MouseDown(corner, MouseButton.Left, RawInputModifiers.None);
        e.Window.MouseMove(corner + new Point(40 * zoom, 10 * zoom), RawInputModifiers.None);
        e.Window.MouseUp(corner + new Point(40 * zoom, 10 * zoom), MouseButton.Left, RawInputModifiers.None);
        double resizedSize = ((TextItem)e.Editor.State.FindItem(date.Id)!).FontSize;
        Assert.True(resizedSize > TextItem.DefaultFontSize, "The date was not resized");

        e.Editor.SelectToolCommand.Execute(EditorTool.Date);
        e.Click(200, 400);
        TextItem next = e.Editor.State.Items.OfType<TextItem>().Last();
        Assert.Equal(resizedSize, next.FontSize, 3);
    }

    [AvaloniaFact]
    public async Task Colour_and_size_apply_to_the_selection_and_become_the_default()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(100, 150);
        e.Editor.SelectToolCommand.Execute(EditorTool.Select);
        e.Click(100, 150);
        Assert.Equal("Medium", e.Editor.SizeLabel);
        Assert.Equal("14 pt", e.Editor.SizeTip);

        e.Editor.SetColourCommand.Execute(InkColor.Red);
        e.Editor.LargerCommand.Execute(null);
        e.Editor.LargerCommand.Execute(null);
        MarkItem tick = e.Editor.State.Items.OfType<MarkItem>().Single();
        Assert.Equal(InkColor.Red, tick.Color);
        Assert.Equal(18, tick.Size);
        Assert.Equal("Large", e.Editor.SizeLabel);
        Assert.True(e.Editor.IsRedChosen);

        // New ticks get the same colour and size.
        e.Editor.SelectToolCommand.Execute(EditorTool.Tick);
        e.Click(100, 250);
        MarkItem next = e.Editor.State.Items.OfType<MarkItem>().Last();
        Assert.Equal((InkColor.Red, 18.0), (next.Color, next.Size));
    }

    [AvaloniaFact]
    public async Task Narrow_windows_collapse_tools_but_keep_Tick_Text_and_Sign_labelled()
    {
        using var e = await EditorSession.OpenAsync(width: 1024);
        Assert.True(e.View.FindControl<Button>("MoreButton")!.IsVisible);
        Assert.False(e.View.FindControl<Button>("ShapesButton")!.IsVisible);
        await e.SaveAsync("edit-narrow-window");
    }

    private static SignatureImage TestSignature()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(440, 120));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1d, 0x3f, 0x9e)), 6, lineCap: PenLineCap.Round);
            var path = StreamGeometry.Parse("M12 88 C40 20 60 0 70 20 S50 100 90 80 S140 30 160 60 S220 90 260 50 S340 40 430 70");
            context.DrawGeometry(null, pen, path);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return new SignatureImage(Guid.NewGuid(), [.. stream.ToArray()], 440, 120);
    }

    /// <summary>Bounds of ink-blue pixels in a region of a frame (the page outline and caret are other colours).</summary>
    private static (int Left, int Top, int Right, int Bottom) InkBounds(WriteableBitmap frame, PixelRect region)
    {
        using Avalonia.Platform.ILockedFramebuffer buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        bool isRgba = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            for (int x = region.X; x < region.Right; x++)
            {
                int i = (y * buffer.RowBytes) + (x * 4);
                int r = bytes[isRgba ? i : i + 2], g = bytes[i + 1], b = bytes[isRgba ? i + 2 : i];
                if (b - r > 60 && b < 220 && g < 150)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        Assert.True(right >= 0, "no ink found");
        return (left, top, right, bottom);
    }

    /// <summary>An open practice form with its window, view and page canvas, plus pointer and key helpers.</summary>
    private sealed class EditorSession : IDisposable
    {
        private EditorSession(MainWindow window, EditorViewModel editor, EditorView view, PagesCanvas canvas)
        {
            Window = window;
            Editor = editor;
            View = view;
            Canvas = canvas;
        }

        public MainWindow Window { get; }

        public EditorViewModel Editor { get; }

        public EditorView View { get; }

        public PagesCanvas Canvas { get; }

        public TextBox? TypingBox => Canvas.Children.OfType<TextBox>().FirstOrDefault(box => box.IsVisible);

        public static async Task<EditorSession> OpenAsync(double width = 1280, string file = "sample-form.pdf")
        {
            // Each test starts from the default ink, not whatever the last test chose.
            Platform.AppSettings.Update(settings => settings with { InkColour = null });
            var window = new MainWindow(isFirstLaunch: false, []) { Width = width, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample(file)]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading && editor.Renderer.TryGet(0, Rotation.None, editor.Zoom) is { } page && Math.Abs(page.Scale - editor.Zoom) < 0.001);
            var view = (EditorView)window.FindControl<ContentControl>("Host")!.Content!;
            return new EditorSession(window, editor, view, view.FindControl<PagesCanvas>("Canvas")!);
        }

        public Point ToWindow(double x, double y) => Canvas.TranslatePoint(Canvas.PageToCanvas(0, new PagePoint(x, y)), Window)!.Value;

        public void Down(double x, double y) => Window.MouseDown(ToWindow(x, y), MouseButton.Left, RawInputModifiers.None);

        public void Move(double x, double y) => Window.MouseMove(ToWindow(x, y), RawInputModifiers.LeftMouseButton);

        public void Up(double x, double y) => Window.MouseUp(ToWindow(x, y), MouseButton.Left, RawInputModifiers.None);

        public void Click(double x, double y, int clickCount = 1)
        {
            for (int i = 0; i < clickCount; i++)
            {
                Down(x, y);
                Up(x, y);
            }
        }

        public void Drag(params (double X, double Y)[] path)
        {
            Down(path[0].X, path[0].Y);
            for (int i = 1; i < path.Length; i++)
            {
                // Several steps per segment, like a real pointer.
                (double X, double Y) from = path[i - 1], to = path[i];
                for (int step = 1; step <= 5; step++)
                {
                    Move(from.X + ((to.X - from.X) * step / 5), from.Y + ((to.Y - from.Y) * step / 5));
                }
            }

            Up(path[^1].X, path[^1].Y);
        }

        public void Key(Key key, RawInputModifiers modifiers)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        public void FocusPage() => View.FindControl<ScrollViewer>("Scroller")!.Focus();

        public async Task<WriteableBitmap> FrameAsync()
        {
            await Pump(() => true);
            return Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame.");
        }

        /// <summary>The text item's box in frame pixels.</summary>
        public PixelRect ItemRegion(TextItem text)
        {
            var size = Core.Geometry.ItemGeometry.Size(text, Editor.Measurer);
            Point topLeft = ToWindow(text.X, text.Y);
            Point bottomRight = ToWindow(text.X + size.Width, text.Y + size.Height);
            return new PixelRect((int)topLeft.X - 2, (int)topLeft.Y, (int)(bottomRight.X - topLeft.X) + 4, (int)(bottomRight.Y - topLeft.Y));
        }

        public async Task SaveAsync(string name)
        {
            await Pump(() => true);
            Save(Window, name);
        }

        public void Dispose() => Window.CloseWithoutAsking();
    }
}
