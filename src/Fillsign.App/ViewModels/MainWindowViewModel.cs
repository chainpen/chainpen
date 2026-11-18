using CommunityToolkit.Mvvm.ComponentModel;

namespace Fillsign.App.ViewModels;

/// <summary>A window shows the start screen until a document is open, then that document's editor.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(bool isFirstLaunch)
    {
        Start = new StartViewModel(isFirstLaunch);
        Content = Start;
    }

    public StartViewModel Start { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Editor), nameof(Title))]
    public partial ObservableObject Content { get; private set; }

    public EditorViewModel? Editor => Content as EditorViewModel;

    public string Title => Editor is { } editor ? $"{editor.Title} – Fillsign" : "Fillsign";

    public void ShowEditor(EditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        Content = editor;

        // The window title follows the document's name, which Save as… can change.
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Title) && ReferenceEquals(Editor, editor))
            {
                OnPropertyChanged(nameof(Title));
            }
        };
    }
}
