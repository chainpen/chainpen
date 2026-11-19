using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Fillsign.App.Views;

/// <summary>
/// The list of shortcuts (FR-7.13, E8). Keys are shown the way this computer labels them: ⌘ on a Mac, Ctrl elsewhere.
/// </summary>
public partial class ShortcutsPanel : UserControl
{
    /// <summary>(group, keys, what it does). "Mod" becomes ⌘ or Ctrl.</summary>
    public static readonly IReadOnlyList<(string Group, string[] Keys, string Action)> Shortcuts =
    [
        ("Editing", ["Mod C", "Mod X"], "Copy, cut items or pages"),
        ("Editing", ["Mod V"], "Paste (also text from other apps)"),
        ("Editing", ["Mod D"], "Duplicate"),
        ("Editing", ["Mod B"], "Bold text"),
        ("Editing", ["Delete"], "Delete the selection"),
        ("Editing", ["Mod Z", "Mod Shift Z"], "Undo, redo"),
        ("Selecting", ["Mod A"], "Select everything on this page (all pages when organising)"),
        ("Selecting", ["Shift or Mod + click"], "Add to or remove from the selection"),
        ("Selecting", ["← → ↑ ↓"], "Nudge (hold Shift for bigger steps)"),
        ("Selecting", ["Esc"], "Clear the selection, or stop what you’re doing"),
        ("Tools", ["V", "T", "C", "X"], "Select, text, tick, cross"),
        ("Tools", ["D", "S", "R", "P"], "Date, sign, shapes, draw (can be turned off in Settings)"),
        ("Document", ["Mod O"], "Open"),
        ("Document", ["Mod S", "Mod Shift S"], "Save working copy, save as"),
        ("Document", ["Mod E"], "Export final copy"),
        ("Document", ["Mod P"], "Print"),
        ("Document", ["Mod +", "Mod −", "Mod 0"], "Zoom in, out, fit width"),
        ("Document", ["Page Up", "Page Down"], "Previous, next page"),
        ("Document", ["F6"], "Move between toolbar, page list, page and status bar"),
        ("Document", ["Mod ,"], "Settings"),
    ];

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
            Avalonia.Automation.AutomationProperties.SetName(row, $"{string.Join(", ", shortcut.Keys.Select(ForThisComputer))}: {shortcut.Action}");
            Rows.Children.Add(row);
        }
    }

    /// <summary>"Mod S" → "⌘ S" on a Mac, "Ctrl S" elsewhere.</summary>
    public static string ForThisComputer(string keys) =>
        keys.Replace("Mod", OperatingSystem.IsMacOS() ? "⌘" : "Ctrl", StringComparison.Ordinal);
}
