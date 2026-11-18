using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fillsign.App.Controls;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;
using Fillsign.Core.Model;

using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>Phase 3: tools, items and selection, driven with real pointer and key input on the practice form.</summary>
public sealed class EditingTests(ITestOutputHelper output)
{
    private static readonly RawInputModifiers s_command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

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
        Assert.Equal(InkColor.Blue, ticks[0].Color);
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
    public async Task Double_click_edits_text_with_the_Select_tool()
    {
        using var e = await EditorSession.OpenAsync();
        e.Editor.SelectToolCommand.Execute(EditorTool.Date);
        e.Click(200, 150);
        TextItem date = Assert.Single(e.Editor.State.Items.OfType<TextItem>());
        Assert.Matches(@"^\d\d/\d\d/\d{4}$", date.Text);
        Assert.Null(e.Editor.TypingItem);

        e.Editor.SelectToolCommand.Execute(EditorTool.Select);
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
    public async Task Things_inside_an_outlined_shape_can_still_be_selected()
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

        // Inside the rectangle but away from the tick is empty page; the outline itself grabs the rectangle.
        e.Click(320, 150);
        Assert.Empty(e.Editor.SelectedIds);
        e.Click(300, 140);
        Assert.IsType<ShapeItem>(Assert.Single(e.Editor.SelectedItems()));
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
        Assert.Equal(ContextMode.Notice, e.Editor.ContextMode);
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

        public static async Task<EditorSession> OpenAsync(double width = 1280)
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = width, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
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

        public void Dispose() => Window.Close();
    }
}
