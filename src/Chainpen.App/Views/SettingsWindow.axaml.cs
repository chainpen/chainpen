using Avalonia.Controls;
using Chainpen.App.ViewModels;

namespace Chainpen.App.Views;

/// <summary>Settings (FS-706, 🎨 7). One window for the app; changes apply to every open window straight away.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var viewModel = new SettingsViewModel(MainWindow.SignatureStore, ConfirmAsync);
        DataContext = viewModel;
        Opened += async (_, _) =>
        {
            Platform.WindowFit.KeepOnScreen(this);

            // Reading saved signatures can ask for Keychain or keyring access, so keep it off the UI thread.
            await Task.Run(MainWindow.SignatureStore.Load).ConfigureAwait(true);
            viewModel.LoadSignatures();
        };
    }

    internal SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    /// <summary>Puts focus back on the language list, where it was when the language changed.</summary>
    internal void FocusLanguage() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LanguageChoice.Focus(Avalonia.Input.NavigationMethod.Tab), Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>A red-button confirmation over the window, starting on Cancel (INTERACTION-PATTERNS §5).</summary>
    private async Task<bool> ConfirmAsync(string title, string body, string destructiveLabel)
    {
        var dialog = new ChoiceDialog(title, body, destructiveLabel, otherLabel: null, focusMain: false, isMainDestructive: true);
        DialogHost.Content = dialog;
        DialogLayer.IsVisible = true;
        try
        {
            return await dialog.Result.ConfigureAwait(true) == Choice.Main;
        }
        finally
        {
            DialogLayer.IsVisible = false;
            DialogHost.Content = null;
        }
    }
}
