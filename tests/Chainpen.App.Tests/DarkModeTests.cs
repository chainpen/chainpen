using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Chainpen.App.Platform.Signatures;
using Chainpen.App.ViewModels;
using Chainpen.App.Views;
using Chainpen.Core.Model;

using static Chainpen.App.Tests.UiTest;

namespace Chainpen.App.Tests;

/// <summary>FS-807: the newer screens in the dark appearance, saved for review (the page itself stays light).</summary>
public sealed class DarkModeTests
{
    [AvaloniaFact]
    public async Task Organise_save_menu_notices_and_signature_dialog_in_dark()
    {
        Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.Dark });
        await Pump(() => Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
        MainWindow.SignatureStoreOverride = new SavedSignatureStore(Path.Combine(Path.GetTempPath(), "chainpen-tests", Guid.NewGuid().ToString("N")), new NoKeyVault());
        try
        {
            var window = new MainWindow(isFirstLaunch: false, []) { Width = 1280, Height = 860 };
            window.Show();
            await window.OpenPathsAsync([TestFiles.Sample("sample-form.pdf")]);
            EditorViewModel editor = Editor(window);
            await Pump(() => !editor.IsLoading);
            editor.SelectToolCommand.Execute(EditorTool.Tick);
            editor.PointerPressed(0, new PagePoint(62, 128), isShift: false, isCommand: false, clickCount: 1, tolerance: 4);
            editor.PointerReleased(new PagePoint(62, 128));
            editor.SelectToolCommand.Execute(EditorTool.Select);
            editor.SelectAllOnCurrentPage();
            Platform.AppUpdates.Instance.ShowReady(new Platform.UpdateReady("1.1", ["Faster opening for large scans"]));
            await Pump(() => true);
            Save(window, "phase8-dark-editor");
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);

            editor.ShowOrganiseCommand.Execute(null);
            editor.SetPageSelection([editor.State.Pages[0].Id]);
            await Pump(() => true);
            Save(window, "phase8-dark-organise");

            editor.ShowEditContentCommand.Execute(null);
            editor.SelectToolCommand.Execute(EditorTool.Sign);
            await Pump(() => window.FindControl<ContentControl>("DialogHost")!.Content is SignatureDialog);
            ((SignatureDialogViewModel)((SignatureDialog)window.FindControl<ContentControl>("DialogHost")!.Content!).DataContext!).Tab = SignatureTab.Type;
            await Pump(() => true);
            Save(window, "phase8-dark-signature");
            ((SignatureDialogViewModel)((SignatureDialog)window.FindControl<ContentControl>("DialogHost")!.Content!).DataContext!).CancelCommand.Execute(null);
            window.CloseWithoutAsking();
        }
        finally
        {
            MainWindow.SignatureStoreOverride = null;
            Platform.AppSettings.Update(settings => settings with { Appearance = Platform.AppearanceChoice.MatchSystem });
        }
    }
}
