using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chainpen.App.ViewModels;

namespace Chainpen.App.Views;

/// <summary>The signature dialog (D1–D3, D10, D11). All behaviour lives in <see cref="SignatureDialogViewModel"/>.</summary>
public partial class SignatureDialog : UserControl
{
    private const double FormWidth = 660;
    private const double ConfirmWidth = 536;

    public SignatureDialog()
    {
        InitializeComponent();
        Width = FormWidth;
        KeyDown += OnKeyDown;
        // Focus once laid out: while the dialog layer is still appearing, nothing in it can take focus.
        Loaded += (_, _) => Dispatcher.UIThread.Post(FocusStart, DispatcherPriority.Loaded);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SignatureDialogViewModel viewModel)
            {
                viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    public SignatureDialog(SignatureDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private SignatureDialogViewModel? ViewModel => DataContext as SignatureDialogViewModel;

    /// <summary>Starts on the preselected saved signature, or on the way to make a new one.</summary>
    private void FocusStart()
    {
        if (ViewModel is { HasSaved: true })
        {
            OptionList.ContainerFromItem(OptionList.SelectedItem!)?.Focus(NavigationMethod.Tab);
        }
        else
        {
            Pad.Focus(NavigationMethod.Tab);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(SignatureDialogViewModel.IsConfirmingRemove))
        {
            // The removal question is a small dialog of its own (D10).
            Width = viewModel.IsConfirmingRemove ? ConfirmWidth : FormWidth;
        }

        if (e.PropertyName == nameof(SignatureDialogViewModel.IsConfirmingRemove) && viewModel.IsConfirmingRemove)
        {
            // Destructive confirmations start on Cancel (INTERACTION-PATTERNS §5).
            Dispatcher.UIThread.Post(() => KeepButton.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(SignatureDialogViewModel.Tab) && viewModel.IsTypeTab)
        {
            Dispatcher.UIThread.Post(() => TypedNameBox.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { } viewModel)
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTypedNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } viewModel)
        {
            viewModel.PlaceCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>A photo dropped anywhere on the dialog goes to the Photo tab.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { IsCreating: true } viewModel)
        {
            return;
        }

        string? path = (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().FirstOrDefault();
        if (path is null)
        {
            return;
        }

        e.Handled = true;
        viewModel.Tab = SignatureTab.Photo;
        await viewModel.LoadPhotoAsync(path).ConfigureAwait(true);
    }
}
