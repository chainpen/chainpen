using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fillsign.App.Controls;
using Fillsign.App.Platform.Signatures;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;
using Fillsign.Pdf.Fonts;
using SkiaSharp;

using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>Phase 4: the signature dialog, driven through the Sign tool with real pointer and key input.</summary>
public sealed class SignatureDialogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fillsign-tests", Guid.NewGuid().ToString("N"));
    private readonly SavedSignatureStore _store;

    public SignatureDialogTests()
    {
        _store = new SavedSignatureStore(_folder, new NoKeyVault());
        MainWindow.SignatureStoreOverride = _store;
    }

    public void Dispose()
    {
        MainWindow.SignatureStoreOverride = null;
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task First_signature_is_drawn_and_placing_with_an_empty_pad_says_what_is_needed()
    {
        using var s = await Session.OpenSignAsync();
        Assert.Equal("Add your signature", s.Dialog.Title);
        Assert.False(s.Dialog.HasSaved);
        Assert.False(s.Dialog.KeepOnComputer);

        // FR-4.8: Place stays usable and explains, rather than doing nothing.
        s.ClickButton("Place signature");
        Assert.Equal("Sign in the box first, or use the Type tab.", s.Dialog.Error);
        Assert.True(s.Pad.HasError);
        await Pump(() => true);
        Save(s.Window, "phase4-D1-empty-pad");

        s.DrawOnPad((40, 120), (90, 70), (140, 125), (200, 60), (260, 110), (330, 90));
        Assert.Null(s.Dialog.Error);
        Assert.Single(s.Dialog.Strokes);
        await Pump(() => true);
        Save(s.Window, "phase4-D1-drawn");

        s.ClickButton("Place signature");
        await Pump(() => s.Editor.PlacingSignature is not null);
        Assert.Equal(SignatureKind.Signature, s.Editor.PlacingSignature!.Value.Kind);
        Assert.True(s.Editor.PlacingSignature.Value.Image.PixelWidth > s.Editor.PlacingSignature.Value.Image.PixelHeight);
        Assert.False(s.IsDialogOpen);

        // Not kept: keeping is opt-in.
        Assert.Empty(new SavedSignatureStore(_folder, new NoKeyVault()).Load());
    }

    [AvaloniaFact]
    public async Task Clear_empties_the_pad()
    {
        using var s = await Session.OpenSignAsync();
        s.DrawOnPad((40, 100), (200, 80));
        s.ClickButton("Clear");
        Assert.Empty(s.Dialog.Strokes);
        Assert.False(s.Dialog.HasInk);
    }

    [AvaloniaFact]
    public async Task A_typed_signature_can_be_kept_and_is_then_ready_to_place_in_one_step()
    {
        using (var s = await Session.OpenSignAsync())
        {
            s.Dialog.IsTypeTab = true;
            await Pump(() => s.Dialog.IsTypeTab);
            s.ClickButton("Place signature");
            Assert.Equal("Type your name first.", s.Dialog.Error);

            TextBox name = s.Find<TextBox>("TypedNameBox");
            name.Focus();
            s.Window.KeyTextInput("Jordan Lee");
            Assert.Equal("Jordan Lee", s.Dialog.TypedName);
            Assert.Null(s.Dialog.Error);
            s.Dialog.SelectedStyle = s.Dialog.StyleOptions[1];
            s.Dialog.IsBlueInk = true;
            s.Dialog.KeepOnComputer = true;
            await Pump(() => true);
            Save(s.Window, "phase4-D2-type");

            // Enter in the name box places it.
            s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Pump(() => s.Editor.PlacingSignature is not null);
            s.Editor.CancelPlacingCommand.Execute(null);
        }

        SavedSignature kept = Assert.Single(new SavedSignatureStore(_folder, new NoKeyVault()).Load());
        Assert.Equal(SignatureMethod.Typed, kept.Method);
        Assert.Equal(SignatureKind.Signature, kept.Kind);

        using (var s = await Session.OpenSignAsync())
        {
            Assert.Equal("Add a signature", s.Dialog.Title);
            Assert.True(s.Dialog.HasSaved);
            Assert.Equal(kept.Id, s.Dialog.SelectedSaved?.Id);
            Assert.False(s.Dialog.IsCreating);
            Assert.StartsWith("Last used ", s.Dialog.SavedHint, StringComparison.Ordinal);
            await Pump(() => true);
            Save(s.Window, "phase4-3-choose-saved");

            s.ClickButton("Place signature");
            await Pump(() => s.Editor.PlacingSignature is not null);
            Assert.Equal(kept.Id, s.Editor.PlacingSignature!.Value.Image.Id);
        }
    }

    [AvaloniaFact]
    public async Task The_switch_turns_everything_to_initials_with_a_smaller_pad()
    {
        _store.Keep(new SavedSignature(TestImage(), SignatureKind.Signature, SignatureMethod.Drawn, DateTimeOffset.UtcNow));
        using var s = await Session.OpenSignAsync();
        Assert.True(s.Dialog.HasSaved);

        s.Dialog.IsInitials = true;
        await Pump(() => s.Dialog.IsInitials);
        Assert.Equal("Add your initials", s.Dialog.Title);
        Assert.Equal("Place initials", s.Dialog.PlaceLabel);
        Assert.False(s.Dialog.HasSaved);
        Assert.True(s.Dialog.IsCreating);
        await Pump(() => s.Pad.Bounds.Height > 0);
        Assert.Equal(150, s.Pad.Bounds.Height);
        Assert.Equal(360, s.Pad.Bounds.Width);

        s.ClickButton("Place initials");
        Assert.Equal("Write your initials in the box first, or use the Type tab.", s.Dialog.Error);
        s.DrawOnPad((40, 60), (80, 30), (120, 70));
        await Pump(() => true);
        Save(s.Window, "phase4-D11-initials");
        s.ClickButton("Place initials");
        await Pump(() => s.Editor.PlacingSignature is not null);
        Assert.Equal(SignatureKind.Initials, s.Editor.PlacingSignature!.Value.Kind);
    }

    [AvaloniaFact]
    public async Task Removing_a_saved_signature_asks_first_and_starts_on_Cancel()
    {
        var older = new SavedSignature(TestImage(), SignatureKind.Signature, SignatureMethod.Photo, DateTimeOffset.UtcNow.AddDays(-2));
        var newer = new SavedSignature(TestImage(), SignatureKind.Signature, SignatureMethod.Drawn, DateTimeOffset.UtcNow);
        _store.Keep(older);
        _store.Keep(newer);
        using var s = await Session.OpenSignAsync();
        Assert.Equal(newer.Id, s.Dialog.SelectedSaved?.Id);

        s.ClickButton("Remove this signature…");
        await Pump(() => s.Dialog.IsConfirmingRemove);
        Assert.Equal("Remove this signature?", s.Dialog.RemoveTitle);
        await Pump(() => ReferenceEquals(s.Find<Button>("KeepButton"), s.Window.FocusManager?.GetFocusedElement()));
        Save(s.Window, "phase4-D10-remove");

        // Esc and Cancel go back without removing anything.
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => s.Dialog.IsForm);
        Assert.True(s.IsDialogOpen);
        Assert.Equal(2, _store.Load().Count);

        s.ClickButton("Remove this signature…");
        await Pump(() => s.Dialog.IsConfirmingRemove);
        s.ClickButton("Remove signature");
        await Pump(() => s.Dialog.IsForm);
        Assert.Equal(older.Id, Assert.Single(_store.Load()).Id);
        Assert.Equal(older.Id, s.Dialog.SelectedSaved?.Id);
    }

    [AvaloniaFact]
    public async Task A_photo_is_previewed_with_and_without_its_paper()
    {
        string photo = Path.Combine(_folder, "signature-photo.png");
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(photo, MakePhoto());
        using var s = await Session.OpenSignAsync();
        s.Dialog.IsPhotoTab = true;
        s.ClickButton("Place signature");
        Assert.Equal("Choose a photo or scan of your signature first.", s.Dialog.Error);

        await s.Dialog.LoadPhotoAsync(photo);
        Assert.Equal("signature-photo.png", s.Dialog.PhotoName);
        Assert.True(s.Dialog.RemoveBackground);
        Assert.NotNull(s.Dialog.PhotoOriginal);
        Assert.NotNull(s.Dialog.PhotoResult);
        Assert.Null(s.Dialog.Error);
        int croppedWidth = s.Dialog.PhotoResult!.PixelSize.Width;
        await Pump(() => true);
        Save(s.Window, "phase4-D3-photo");

        s.Dialog.RemoveBackground = false;
        Assert.Equal(600, s.Dialog.PhotoResult!.PixelSize.Width);
        Assert.True(croppedWidth < 600);

        s.Dialog.RemoveBackground = true;
        s.ClickButton("Place signature");
        await Pump(() => s.Editor.PlacingSignature is not null);
    }

    [AvaloniaFact]
    public async Task A_file_that_is_not_a_picture_is_explained()
    {
        string notAPhoto = Path.Combine(_folder, "notes.txt");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(notAPhoto, "hello");
        using var s = await Session.OpenSignAsync();
        s.Dialog.IsPhotoTab = true;
        await s.Dialog.LoadPhotoAsync(notAPhoto);
        Assert.Equal("That file couldn’t be read as a picture. Choose a JPG or PNG photo or scan.", s.Dialog.Error);
        Assert.False(s.Dialog.HasPhoto);
    }

    [AvaloniaFact]
    public async Task Esc_cancels_and_nothing_is_placed()
    {
        using var s = await Session.OpenSignAsync();
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(() => !s.IsDialogOpen);
        Assert.Null(s.Editor.PlacingSignature);
    }

    [AvaloniaFact]
    public async Task With_six_saved_the_keep_option_says_one_will_be_replaced()
    {
        for (int i = 0; i < SavedSignatureStore.MaxPerKind; i++)
        {
            _store.Keep(new SavedSignature(TestImage(), SignatureKind.Signature, SignatureMethod.Drawn, DateTimeOffset.UtcNow.AddDays(-i)));
        }

        using var s = await Session.OpenSignAsync();
        Assert.Equal(SavedSignatureStore.MaxPerKind + 1, s.Dialog.Options.Count);
        s.Dialog.SelectedOption = s.Dialog.Options[^1];
        Assert.True(s.Dialog.IsCreating);
        Assert.Equal("You have 6 saved. Keeping this one replaces the one used longest ago.", s.Dialog.KeepDetail);
    }

    private static SignatureImage TestImage()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(220, 60, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var pen = new SKPaint { Color = new SKColor(0x1d, 0x3f, 0x9e), StrokeWidth = 4, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(10, 45, 210, 15, pen);
        }

        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new SignatureImage(Guid.NewGuid(), [.. png.ToArray()], 220, 60);
    }

    private static byte[] MakePhoto()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(600, 300, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(232, 226, 210));
            using var pen = new SKPaint { Color = new SKColor(40, 40, 60), StrokeWidth = 6, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(80, 180, 520, 120, pen);
        }

        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private sealed class Session : IDisposable
    {
        private Session(MainWindow window, EditorViewModel editor)
        {
            Window = window;
            Editor = editor;
        }

        public MainWindow Window { get; }

        public EditorViewModel Editor { get; }

        public SignatureDialog View => (SignatureDialog)Window.FindControl<ContentControl>("DialogHost")!.Content!;

        public SignatureDialogViewModel Dialog => (SignatureDialogViewModel)View.DataContext!;

        public SignaturePad Pad => Find<SignaturePad>("Pad");

        public bool IsDialogOpen => Window.FindControl<ContentControl>("DialogHost")!.Content is SignatureDialog;

        /// <summary>Opens the practice form, presses Sign and waits for the dialog.</summary>
        public static async Task<Session> OpenSignAsync()
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);
            var session = new Session(window, editor);
            editor.SelectToolCommand.Execute(EditorTool.Sign);
            await Pump(() => session.IsDialogOpen && session.View.Bounds.Width > 0);
            return session;
        }

        public T Find<T>(string name)
            where T : Control => View.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

        public void ClickButton(string content) => Click(View, content);

        /// <summary>Draws one stroke on the pad with the mouse, through points in pad coordinates.</summary>
        public void DrawOnPad(params (double X, double Y)[] path)
        {
            Point ToWindow((double X, double Y) point) => Pad.TranslatePoint(new Point(point.X, point.Y), Window)!.Value;

            Window.MouseDown(ToWindow(path[0]), MouseButton.Left, RawInputModifiers.None);
            for (int i = 1; i < path.Length; i++)
            {
                for (int step = 1; step <= 6; step++)
                {
                    double x = path[i - 1].X + ((path[i].X - path[i - 1].X) * step / 6);
                    double y = path[i - 1].Y + ((path[i].Y - path[i - 1].Y) * step / 6);
                    Window.MouseMove(ToWindow((x, y)), RawInputModifiers.LeftMouseButton);
                }
            }

            Window.MouseUp(ToWindow(path[^1]), MouseButton.Left, RawInputModifiers.None);
        }

        public void Dispose() => Window.Close();
    }
}
