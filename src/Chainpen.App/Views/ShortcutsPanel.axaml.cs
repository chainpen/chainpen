using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Chainpen.App.Localization;

namespace Chainpen.App.Views;

/// <summary>
/// The list of shortcuts (FR-7.13, E8). Keys are shown the way this computer labels them: ⌘ on a Mac, Ctrl elsewhere.
/// </summary>
public partial class ShortcutsPanel : UserControl
{
    /// <summary>
    /// (group, keys, what it does). "Mod" becomes ⌘ or Ctrl. Built when asked for, so it's in the interface language.
    /// </summary>
    public static IReadOnlyList<(string Group, string[] Keys, string Action)> Shortcuts =>
    [
        (Strings.Shortcuts_Editing, ["Mod C", "Mod X"], Strings.Shortcuts_CopyCutItemsOrPages),
        (Strings.Shortcuts_Editing, ["Mod V"], Strings.Shortcuts_PasteAlsoTextFromOtherApps),
        (Strings.Shortcuts_Editing, ["Mod D"], Strings.Editor_Duplicate),
        (Strings.Shortcuts_Editing, ["Mod B"], Strings.Shortcuts_BoldText),
        (Strings.Shortcuts_Editing, [Strings.Keys_Delete], Strings.Shortcuts_DeleteTheSelection),
        (Strings.Shortcuts_Editing, ["Mod Z", "Mod Shift Z"], Strings.Shortcuts_UndoRedo),
        (Strings.Shortcuts_Selecting, ["Mod A"], Strings.Shortcuts_SelectEverythingOnThisPage),
        (Strings.Shortcuts_Selecting, [Strings.Shortcuts_ShiftOrModClick], Strings.Shortcuts_AddToOrRemoveFromTheSelection),
        (Strings.Shortcuts_Selecting, ["← → ↑ ↓"], Strings.Shortcuts_NudgeHoldShiftForBiggerSteps),
        (Strings.Shortcuts_Selecting, ["Esc"], Strings.Shortcuts_ClearTheSelectionOrStop),
        (Strings.Editor_Tools, ["V", "T", "C", "X"], Strings.Shortcuts_SelectTextTickCross),
        (Strings.Editor_Tools, ["D", "S", "R", "P"], Strings.Shortcuts_DateSignShapesDraw),
        (Strings.Shortcuts_Finding, ["Mod F"], Strings.Shortcuts_FindWordsInTheDocument),
        (Strings.Shortcuts_Finding, ["Mod G", "Mod Shift G"], Strings.Shortcuts_NextPreviousMatch),
        (Strings.Editor_Document, ["Mod O"], Strings.Common_Open),
        (Strings.Editor_Document, ["Mod S", "Mod Shift S"], Strings.Shortcuts_SaveWorkingCopySaveAs),
        (Strings.Editor_Document, ["Mod E"], Strings.Common_ExportFinalCopy),
        (Strings.Editor_Document, ["Mod P"], Strings.Common_Print),
        (Strings.Editor_Document, ["Mod +", "Mod −", "Mod 0"], Strings.Shortcuts_ZoomInOutFitWidth),
        (Strings.Editor_Document, [Strings.Keys_PageUp, Strings.Keys_PageDown], Strings.Shortcuts_PreviousNextPage),
        (Strings.Editor_Document, ["F6"], Strings.Shortcuts_MoveBetweenToolbarPageListPage),
        (Strings.Editor_Document, ["Mod ,"], Strings.Start_Settings),
    ];

    /// <summary>In the Help → Keyboard shortcuts dialog the title matches other dialogs' (UX review 9).</summary>
    public void UseDialogHeading()
    {
        Title.Classes.Remove("heading-section");
        Title.Classes.Add("heading-dialog");
    }

    public ShortcutsPanel()
    {
        InitializeComponent();
        string? group = null;
        foreach ((string Group, string[] Keys, string Action) shortcut in Shortcuts)
        {
            if (shortcut.Group != group)
            {
                group = shortcut.Group;
                Rows.Children.Add(new TextBlock { Text = group.ToUpperInvariant(), Classes = { "group" } });
            }

            var keys = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
            foreach (string key in shortcut.Keys)
            {
                keys.Children.Add(new Border
                {
                    Classes = { "kbd" },
                    Child = new TextBlock { Text = ForThisComputer(key) },
                });
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Avalonia.Thickness(0, 2) };
            row.Children.Add(keys);
            var action = new TextBlock { Text = shortcut.Action, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
            Avalonia.Automation.AutomationProperties.SetName(row, Text.Format(Strings.Shortcuts_RowName, string.Join(Strings.Editor_ListSeparator, shortcut.Keys.Select(ForThisComputer)), shortcut.Action));
            Rows.Children.Add(row);
        }
    }

    /// <summary>"Mod S" → "⌘ S" on a Mac, "Ctrl S" elsewhere ("Strg S" in German), as tooltips and menus write them.</summary>
    public static string ForThisComputer(string keys) => Controls.KeysExtension.ForThisComputer(keys, OperatingSystem.IsMacOS());
}
