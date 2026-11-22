using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;
using Fillsign.Pdf.Pdfium;
using Fillsign.Pdf.Signatures;
using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>
/// The screenshots in the README and docs, made from the real app at 1440×900 so they can be made again whenever the
/// interface changes. Off by default; run with <c>FILLSIGN_README_SCREENSHOTS=1</c>, look at every image in
/// <c>docs/images/</c>, and commit the ones that are right.
/// </summary>
public sealed class ReadmeScreenshots
{
    // A common laptop size, and the narrowest at which the toolbar keeps every label (INTERACTION-PATTERNS §3).
    private const int Width = 1440;
    private const int Height = 900;

    private static readonly string s_images = Path.Combine(TestFiles.RepoRoot, "docs", "images");

    // The samples under the names a person's own forms might have; the title strip shows them.
    private static readonly string s_documents = Path.Combine(Path.GetTempPath(), "fillsign-readme-" + Environment.ProcessId);

    private static bool IsOn => Environment.GetEnvironmentVariable("FILLSIGN_README_SCREENSHOTS") == "1";

    [AvaloniaFact]
    public async Task Make_the_readme_screenshots()
    {
        Assert.SkipUnless(IsOn, "Set FILLSIGN_README_SCREENSHOTS=1 to make the README screenshots.");
        Directory.CreateDirectory(s_images);
        try
        {
            await FillAFormAsync();
            await ScannedFormAsync(dark: false, "scanned-form");
            await ScannedFormAsync(dark: true, "scanned-form-dark");
            await ExportAsync();
            await OrganiseAsync();
            await FindAsync();
        }
        finally
        {
            await UseAppearanceAsync(dark: false);
            Directory.Delete(s_documents, recursive: true);
        }
    }

    /// <summary>A PDF with its own form fields, filled in.</summary>
    private static async Task FillAFormAsync()
    {
        await UseAppearanceAsync(dark: false);
        (MainWindow window, EditorViewModel editor) = await OpenAsync("form-fields.pdf");
        try
        {
            editor.SetField("Name", "Jordan Lee");
            editor.SetField("Email", "jordan.lee@example.com");
            editor.SetField("DateOfBirth", "14/03/1988");
            editor.SetField("Address", "12 Harbour Street\nNorthside NSW 2060");
            editor.SetField("ContactEmail", OnValue(editor, "ContactEmail"));
            editor.SetField("Membership", "Family");
            editor.SetField("State", "NSW");
            editor.SetField("Postcode", "2060");
            editor.SetField("Heard", PdfiumDocument.JoinChoices(["Friend", "Search"]));
            editor.SetField("Title", "Ms");
            editor.Zoom = 0.95;
            if (SignatureImageMaker.FromText("Jordan Lee", HandwritingStyle.DancingScript, InkColor.Blue) is { } signature)
            {
                // Clicking the signature field fits the signature into it.
                Pdf.Pdfium.FormField field = editor.Source.FormFields(0).First(candidate => candidate.Name == "Signature");
                var transform = new Core.Geometry.PageTransform(editor.Source.GetPage(0), Rotation.None);
                PagePoint centre = transform.ToDisplay((field.Rect.Left + field.Rect.Right) / 2, (field.Rect.Bottom + field.Rect.Top) / 2);
                editor.BeginPlacingSignature(signature, SignatureKind.Signature);
                editor.PointerPressed(0, centre, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
                editor.PointerReleased(centre);
                editor.SelectToolCommand.Execute(EditorTool.Select);
                editor.BackOut();
            }

            await CaptureAsync(window, "fill-a-form");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    /// <summary>A flat form with no fields: ticks, typed text and a signature placed on the page.</summary>
    private static async Task ScannedFormAsync(bool dark, string name)
    {
        await UseAppearanceAsync(dark);
        (MainWindow window, EditorViewModel editor) = await OpenAsync("sample-form.pdf");
        try
        {
            await FillTheSampleAsync(window, editor, signs: true);
            await CaptureAsync(window, name);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    /// <summary>Export final copy, pointing out what may still be missing.</summary>
    private static async Task ExportAsync()
    {
        await UseAppearanceAsync(dark: false);
        (MainWindow window, EditorViewModel editor) = await OpenAsync("sample-form.pdf");
        try
        {
            await FillTheSampleAsync(window, editor, signs: false);
            editor.ExportCommand.Execute(null);
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is ExportDialog);
            var dialog = (ExportDialog)window.FindControl<ContentControl>("DialogHost")!.Content!;
            dialog.UseFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"));
            var savesTo = dialog.FindControl<TextBlock>("SavesTo")!;
            Assert.Equal("Saves to ~/Documents", string.Concat(savesTo.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text)));
            await CaptureAsync(window, "export");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    /// <summary>Organise pages: three files opened as one document, a page selected and its actions.</summary>
    private static async Task OrganiseAsync()
    {
        await UseAppearanceAsync(dark: false);
        (MainWindow window, EditorViewModel editor) = await OpenAsync("sample-form.pdf", "form-fields.pdf", "four-pages.pdf");
        try
        {
            editor.ShowOrganiseCommand.Execute(null);
            await Pump(() => editor.IsOrganising);
            editor.SetPageSelection([editor.Pages[2].Id]);
            await CaptureAsync(window, "organise-pages");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    /// <summary>Find, with the matches highlighted on the page and counted in the Pages panel.</summary>
    private static async Task FindAsync()
    {
        await UseAppearanceAsync(dark: false);
        (MainWindow window, EditorViewModel editor) = await OpenAsync("sample-form.pdf");
        try
        {
            editor.OpenFindCommand.Execute(null);
            editor.FindQuery = "over 18";
            await Pump(() => editor.FindHits.Count > 0 && !editor.IsSearching);
            await CaptureAsync(window, "find");
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    /// <summary>
    /// Ticks two of the three boxes and types the name; with <paramref name="signs"/>, signs it too. The places are
    /// the sample's own, in page points: the boxes at x 67, the Name and Signature lines from x 110.
    /// </summary>
    private static async Task FillTheSampleAsync(MainWindow window, EditorViewModel editor, bool signs)
    {
        Click(editor, EditorTool.Tick, new PagePoint(67, 135));
        Click(editor, EditorTool.Tick, new PagePoint(67, 215));

        Click(editor, EditorTool.Text, new PagePoint(118, 296));
        window.KeyTextInput("Jordan Lee");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        if (signs && SignatureImageMaker.FromText("Jordan Lee", HandwritingStyle.DancingScript, InkColor.Blue) is { } signature)
        {
            editor.BeginPlacingSignature(signature, SignatureKind.Signature);
            var at = new PagePoint(200, 346);
            editor.PointerPressed(0, at, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(at);
        }

        editor.SelectToolCommand.Execute(EditorTool.Select);
        editor.BackOut();
        await Pump(() => true);
    }

    private static void Click(EditorViewModel editor, EditorTool tool, PagePoint point)
    {
        editor.SelectToolCommand.Execute(tool);
        editor.PointerPressed(0, point, isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
        editor.PointerReleased(point);
    }

    private static string OnValue(EditorViewModel editor, string field) =>
        editor.Source.FormFields(0).First(candidate => candidate.Name == field).OnValue;

    private static async Task<(MainWindow Window, EditorViewModel Editor)> OpenAsync(params string[] samples)
    {
        Directory.CreateDirectory(s_documents);
        var named = new List<string>();
        foreach (string sample in samples)
        {
            string path = Path.Combine(s_documents, sample switch
            {
                "form-fields.pdf" => "membership-form.pdf",
                "sample-form.pdf" => "consent-form.pdf",
                "four-pages.pdf" => "lease-agreement.pdf",
                _ => sample,
            });
            File.Copy(TestFiles.Sample(sample), path, overwrite: true);
            named.Add(path);
        }

        var window = new MainWindow(isFirstLaunch: false, []) { Width = Width, Height = Height };
        window.Show();
        await window.OpenPathsAsync(named);
        await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });
        await Pump(() => true);

        // Fitting the window's width makes a page far bigger than anyone reads it at.
        EditorViewModel editor = Editor(window);
        editor.Zoom = 1.25;
        return (window, editor);
    }

    private static async Task UseAppearanceAsync(bool dark)
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = dark ? Platform.AppearanceChoice.Dark : Platform.AppearanceChoice.Light });
        ThemeVariant wanted = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        await Pump(() => Avalonia.Application.Current!.ActualThemeVariant == wanted);
    }

    private static async Task CaptureAsync(MainWindow window, string name)
    {
        // A few frames, so thumbnails and pages have finished rendering.
        for (int i = 0; i < 5; i++)
        {
            await Pump(() => true);
        }

        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(Width, frame.PixelSize.Width);
        using FileStream file = File.Create(Path.Combine(s_images, name + ".png"));
        frame.Save(file, PngBitmapEncoderOptions.Default);
    }
}
