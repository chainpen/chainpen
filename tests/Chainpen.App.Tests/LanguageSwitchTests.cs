using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Chainpen.App.Localization;
using Chainpen.App.Platform;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>A language chosen in Settings applies straight away, over the open document, without losing anything.</summary>
public sealed class LanguageSwitchTests
{
    [AvaloniaFact]
    public async Task Choosing_a_language_rebuilds_the_open_windows_in_it_and_keeps_the_document()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1400, Height = 860 };
        window.Show();
        try
        {
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            await Pump(() => window.DataContext is MainWindowViewModel { Editor.IsLoading: false });
            EditorViewModel editor = Editor(window);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            ((EditorView)window.FindControl<ContentControl>("Host")!.Content!).ZoomIn();
            await Pump(() => true);
            double zoom = editor.Zoom;
            Assert.Equal("Tick", ToolName(window, "Tick"));

            MainWindow.ShowSettings();
            SettingsWindow settings = await Await(OpenSettings);
            settings.ViewModel.SelectedLanguage = settings.ViewModel.LanguageOptions.Single(option => option.Code == "de");

            await Pump(() => ToolName(window, "Häkchen") is not null);
            Assert.StartsWith("Kästchen", editor.ToolHint, StringComparison.Ordinal);
            Assert.Equal("Datei", NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().First().Header);
            Assert.Same(editor, Editor(window));
            Assert.Equal(EditorTool.Tick, editor.Tool);
            Assert.Equal(zoom, editor.Zoom, 3);
            Save(window, "language-switch-german");

            // Settings opens again in German, with focus back on the language list.
            SettingsWindow german = await Await(() => OpenSettings() is { } open && open != settings ? open : null);
            Assert.Equal("Chainpen-Einstellungen", german.Title);
            await Pump(() => german.FindControl<ComboBox>("LanguageChoice")!.IsFocused);
            Assert.Equal("de", AppSettings.Load().Language);

            german.ViewModel.SelectedLanguage = german.ViewModel.LanguageOptions.Single(option => option.Code == "en");
            await Pump(() => ToolName(window, "Tick") is not null);
            Assert.Equal("en", Languages.Running);
            OpenSettings()?.Close();
        }
        finally
        {
            OpenSettings()?.Close();
            Languages.Apply("en");
            AppSettings.Update(settings => settings with { Language = null });
            window.CloseWithoutAsking();
        }
    }

    private static SettingsWindow? OpenSettings() => MainWindow.OpenSettings;

    /// <summary>The accessible name of the toolbar's tool button with this name, or null when there's none.</summary>
    private static string? ToolName(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>()
            .Select(Avalonia.Automation.AutomationProperties.GetName)
            .FirstOrDefault(found => found == name);

    private static async Task<T> Await<T>(Func<T?> find)
        where T : class
    {
        T? found = null;
        await Pump(() => (found = find()) is not null);
        return found!;
    }
}
