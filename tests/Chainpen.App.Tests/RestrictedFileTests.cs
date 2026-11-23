using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;
using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>FS-214: a file whose owner forbids changes is never edited without asking.</summary>
public sealed class RestrictedFileTests
{
    [AvaloniaFact]
    public async Task Opening_asks_and_View_only_changes_nothing_until_Edit_anyway()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            Task opening = window.OpenPathsAsync([TestFiles.Sample("corpus/empty_protected.pdf")]);
            await Pump(() => DialogOf(window) is ChoiceDialog);
            ChoiceDialog dialog = (ChoiceDialog)DialogOf(window)!;
            Assert.Equal("empty_protected.pdf can’t be changed", dialog.FindControl<TextBlock>("Heading")!.Text);
            await Pump(() => true);
            Save(window, "phase2-fs214-restricted");
            Click(dialog, "View only");
            await opening;
            await Pump(() => window.DataContext is MainWindowViewModel { Editor: { IsLoading: false } });

            EditorViewModel editor = Editor(window);
            Assert.True(editor.IsViewOnly);

            // Tools stay on Select and a click places nothing.
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            Assert.Equal(EditorTool.Select, editor.Tool);
            editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(100, 100));
            Assert.Empty(editor.State.Items);

            // Edit anyway, from the bar: now it can be filled in.
            editor.EditAnywayCommand.Execute(null);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(100, 100), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(100, 100));
            Assert.Single(editor.State.Items);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    [AvaloniaFact]
    public async Task Dont_open_leaves_the_start_screen()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        try
        {
            Task opening = window.OpenPathsAsync([TestFiles.Sample("corpus/empty_protected.pdf")]);
            await Pump(() => DialogOf(window) is ChoiceDialog);
            Click(DialogOf(window)!, "Don’t open");
            await opening;
            Assert.Null(((MainWindowViewModel)window.DataContext!).Editor);
        }
        finally
        {
            window.CloseWithoutAsking();
        }
    }

    private static Control? DialogOf(MainWindow window) => window.FindControl<ContentControl>("DialogHost")!.Content as Control;
}
