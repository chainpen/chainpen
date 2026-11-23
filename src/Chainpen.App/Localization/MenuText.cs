using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Chainpen.App.Localization;

/// <summary>
/// Menu items that know where their names come from, so a change of language renames them in place: for the macOS
/// app menu, which macOS reads only once. Its items have no submenus, so renaming them is enough (a window's menu bar
/// shows submenu titles, which need new items: <c>MainWindow.RenameMenu</c>).
/// </summary>
internal static class MenuText
{
    // Weak, so menus that are thrown away (a closed window's) don't stay alive here.
    private static readonly ConditionalWeakTable<NativeMenuItem, Func<string>> s_names = [];

    /// <summary>A menu item named by <paramref name="name"/>, in the current language now and after a change.</summary>
    public static NativeMenuItem Item(Func<string> name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var item = new NativeMenuItem(name());
        s_names.Add(item, name);
        return item;
    }

    /// <summary>Renames every item in <paramref name="menu"/> and its submenus that was made with <see cref="Item"/>.</summary>
    public static void Rename(NativeMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>())
        {
            if (s_names.TryGetValue(item, out Func<string>? name))
            {
                item.Header = name();
            }

            if (item.Menu is { } submenu)
            {
                Rename(submenu);
            }
        }
    }
}
