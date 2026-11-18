namespace Fillsign.App.ViewModels;

/// <summary>The tools on the toolbar (FS-300). The shape drawn by <see cref="Shape"/> is chosen from the Shapes menu.</summary>
public enum EditorTool
{
    Select,
    Text,
    Tick,
    Cross,
    Date,
    Sign,
    Shape,
    Draw,
}

/// <summary>What the bar under the toolbar shows (INTERACTION-PATTERNS §2).</summary>
public enum ContextMode
{
    /// <summary>A hint for the Select tool.</summary>
    Hint,

    /// <summary>A placing tool with nothing selected: its colour, size and a hint.</summary>
    ToolOptions,

    /// <summary>What's selected and its actions.</summary>
    Selection,

    /// <summary>Text being typed: size, colour and how to finish (E2).</summary>
    Typing,

    /// <summary>The pen: colour, line width and Done (E6).</summary>
    Drawing,

    /// <summary>Placing a signature: one instruction and Cancel (E4), blue tint.</summary>
    Placing,

    /// <summary>Dragging a selection box (E5).</summary>
    BoxSelecting,

    /// <summary>Something was just deleted: offer Undo straight away (FR-7.11).</summary>
    Notice,
}
