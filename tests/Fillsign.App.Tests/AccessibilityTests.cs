using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Fillsign.App.Platform.Signatures;
using Fillsign.App.ViewModels;
using Fillsign.App.Views;

using static Fillsign.App.Tests.UiTest;

namespace Fillsign.App.Tests;

/// <summary>
/// FS-806: every control a screen reader or keyboard reaches has a name — its own text, an automation name, or a
/// label it's tied to (NFR-5). Checked on the real screens, so a new icon-only button without a name fails here.
/// </summary>
public sealed class AccessibilityTests
{
    [AvaloniaFact]
    public async Task The_start_screen_names_everything()
    {
        var window = new MainWindow(isFirstLaunch: true, []) { Width = 1280, Height = 860 };
        window.Show();
        await Pump(() => true);
        AssertAllNamed(window);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task The_editor_names_everything_in_both_views()
    {
        var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
        window.Show();
        await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
        EditorViewModel editor = Editor(window);
        await Pump(() => !editor.IsLoading);
        AssertAllNamed(window);

        editor.ShowOrganiseCommand.Execute(null);
        editor.SelectAllPagesCommand.Execute(null);
        await Pump(() => true);
        AssertAllNamed(window);
        window.CloseWithoutAsking();
    }

    [AvaloniaFact]
    public async Task Dialogs_and_settings_name_everything()
    {
        MainWindow.SignatureStoreOverride = new SavedSignatureStore(Path.Combine(Path.GetTempPath(), "fillsign-tests", Guid.NewGuid().ToString("N")), new NoKeyVault());
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);

            editor.SelectToolCommand.Execute(EditorTool.Sign);
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is SignatureDialog);
            var dialog = (SignatureDialog)window.FindControl<ContentControl>("DialogHost")!.Content!;
            foreach (SignatureTab tab in Enum.GetValues<SignatureTab>())
            {
                ((SignatureDialogViewModel)dialog.DataContext!).Tab = tab;
                await Pump(() => true);
                AssertAllNamed(dialog);
            }

            ((SignatureDialogViewModel)dialog.DataContext!).CancelCommand.Execute(null);
            window.CloseWithoutAsking();

            var settings = new SettingsWindow();
            settings.Show();
            await Pump(() => true);
            AssertAllNamed(settings);
            settings.Close();
        }
        finally
        {
            MainWindow.SignatureStoreOverride = null;
        }
    }

    [AvaloniaFact]
    public void The_check_catches_an_icon_button_without_a_name()
    {
        var unnamed = new Button { Content = new Controls.StrokeIcon() };
        var named = new Button { Content = new Controls.StrokeIcon() };
        AutomationProperties.SetName(named, "Zoom in");
        var window = new Window { Content = new StackPanel { Children = { unnamed, named, new Button { Content = "Open…" } } } };
        window.Show();
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertAllNamed(window));
        AutomationProperties.SetName(unnamed, "Zoom out");
        AssertAllNamed(window);
        window.Close();
    }

    private static void AssertAllNamed(Control root)
    {
        Control[] checkedControls = [.. root.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && IsInteractive(control))];

        // Guards the check itself: a screen with nothing found would pass trivially.
        Assert.True(checkedControls.Length >= 3, $"only {checkedControls.Length} controls found to check");
        string[] unnamed = [.. checkedControls.Where(control => !HasName(control)).Select(Describe)];
        Assert.True(unnamed.Length == 0, "Controls without a name:\n" + string.Join("\n", unnamed));
    }

    // Controls a person tabs to or a screen reader announces as something to use. List items are named by their
    // content's automation name, checked through the item template.
    private static bool IsInteractive(Control control) =>
        control is Button or ToggleButton or TextBox or ComboBox or Slider && control.Focusable
        && control.TemplatedParent is not (ScrollBar or ComboBox or NumericUpDown or TextBox or Expander);

    private static bool HasName(Control control)
    {
        // A label next to a radio button or check box names it.
        if (control is ContentControl { Content: Control content } && content.GetSelfAndLogicalDescendants().OfType<TextBlock>().Any(block => !string.IsNullOrWhiteSpace(block.Text)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) || AutomationProperties.GetLabeledBy(control) is not null)
        {
            return true;
        }

        // A button's own words, or text shown inside it.
        if (control is ContentControl { Content: string text } && !string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return control.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            || control.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text));
    }

    private static string Describe(Control control)
    {
        IEnumerable<string> path = control.GetSelfAndVisualAncestors().OfType<Control>().Take(5)
            .Select(c => c.Name is { Length: > 0 } name ? $"{c.GetType().Name}#{name}" : c.GetType().Name);
        return string.Join(" < ", path);
    }
}
