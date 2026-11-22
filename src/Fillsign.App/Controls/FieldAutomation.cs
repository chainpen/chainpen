using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Fillsign.App.Localization;
using Fillsign.App.ViewModels;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.App.Controls;

/// <summary>
/// The page as screen readers see it (FS-950, NFR-5): its own children, plus the PDF's form fields on the pages
/// around the one in view, each as the control it is. Screen readers can then list, explore and fill the fields
/// with their own commands, not only hear them as Tab reaches them (WCAG 4.1.2).
/// </summary>
internal sealed class PagesCanvasAutomationPeer(PagesCanvas owner) : ControlAutomationPeer(owner)
{
    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore()
    {
        IReadOnlyList<AutomationPeer>? own = base.GetChildrenCore();
        if (owner.Editor is not { } editor)
        {
            return own;
        }

        var children = new List<AutomationPeer>(own ?? []);
        foreach (FieldHit hit in editor.FieldsNearCurrentPage())
        {
            FieldAutomationPeer peer = FieldAutomationPeer.For(owner, editor, hit);
            peer.AdoptBy(this);
            children.Add(peer);
        }

        return children;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    /// <summary>The page in view changed, so the fields near it did.</summary>
    public void RefreshFields() => InvalidateChildren();
}

/// <summary>One form field for screen readers: its name, kind and value, read live from the editor.</summary>
internal abstract class FieldAutomationPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : AutomationPeer
{
    private AutomationPeer? _parent;

    protected PagesCanvas Canvas { get; } = canvas;

    protected EditorViewModel Editor { get; } = editor;

    protected FieldHit Hit { get; } = hit;

    protected FormField Field => Hit.Field;

    protected string Value => Editor.FieldValue(Field);

    public static FieldAutomationPeer For(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) => hit.Field.Kind switch
    {
        FormFieldKind.CheckBox => new CheckBoxFieldPeer(canvas, editor, hit),
        FormFieldKind.Radio => new RadioFieldPeer(canvas, editor, hit),
        FormFieldKind.Choice => new ChoiceFieldPeer(canvas, editor, hit),
        FormFieldKind.Signature => new SignatureFieldPeer(canvas, editor, hit),
        _ => new TextFieldPeer(canvas, editor, hit),
    };

    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }

    /// <summary>The page's peer adopting this field.</summary>
    public void AdoptBy(AutomationPeer parent) => TrySetParent(parent);

    protected override AutomationPeer? GetParentCore() => _parent;

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];

    protected override string? GetNameCore() => Editor.AccessibleName(Hit);

    protected override string? GetAutomationIdCore() => Field.Kind == FormFieldKind.Radio ? $"{Field.Name}:{Field.OnValue}" : Field.Name;

    protected override string GetClassNameCore() => "FormField";

    protected override Rect GetBoundingRectangleCore() => Canvas.FieldBoundsInWindow(Hit) ?? default;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => !Field.IsReadOnly && !Editor.IsViewOnly;

    protected override bool IsKeyboardFocusableCore() => !Field.IsReadOnly;

    protected override bool HasKeyboardFocusCore() =>
        (Editor.FocusedField ?? Editor.EditingField?.Hit) is { } focused && focused.Page == Hit.Page && focused.Box == Hit.Box;

    protected override void SetFocusCore() => Editor.FocusField(Hit);

    protected override void BringIntoViewCore() => Editor.FocusField(Hit);

    protected override string? GetAcceleratorKeyCore() => null;

    protected override string? GetAccessKeyCore() => null;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override bool ShowContextMenuCore() => false;
}

internal sealed class TextFieldPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : FieldAutomationPeer(canvas, editor, hit), IValueProvider
{
    public bool IsReadOnly => Field.IsReadOnly || Editor.IsViewOnly;

    string? IValueProvider.Value => Value;

    public void SetValue(string? value) => Editor.SetField(Field.Name, value ?? string.Empty);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
}

internal sealed class CheckBoxFieldPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : FieldAutomationPeer(canvas, editor, hit), IToggleProvider
{
    public ToggleState ToggleState => Editor.IsTicked(Field) ? ToggleState.On : ToggleState.Off;

    public void Toggle() => Editor.Activate(Hit);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
}

internal sealed class RadioFieldPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : FieldAutomationPeer(canvas, editor, hit), ISelectionItemProvider
{
    public bool IsSelected => Value == Field.OnValue;

    public ISelectionProvider? SelectionContainer => null;

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
        // A radio button is unchosen by choosing another in its group.
    }

    public void Select() => Editor.Activate(Hit);

    protected override string GetNameCore() => Text.Format(Strings.Editor_RadioButtonName, Field.DisplayName, Field.OnValue);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;
}

internal sealed class ChoiceFieldPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : FieldAutomationPeer(canvas, editor, hit), IValueProvider
{
    public bool IsReadOnly => Field.IsReadOnly || Editor.IsViewOnly;

    string IValueProvider.Value => Field.IsMultiSelect ? string.Join(Strings.Editor_ListSeparator, PdfiumDocument.SplitChoices(Value)) : Value;

    /// <summary>A screen reader setting the value: an option by its name, or a typed answer where the drop-down takes one.</summary>
    public void SetValue(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (Field.Options.Contains(value))
        {
            Editor.ChooseOption(Field, value);
        }
        else if (Field.IsEditable)
        {
            Editor.SetField(Field.Name, value);
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => Field.IsList ? AutomationControlType.List : AutomationControlType.ComboBox;
}

internal sealed class SignatureFieldPeer(PagesCanvas canvas, EditorViewModel editor, FieldHit hit) : FieldAutomationPeer(canvas, editor, hit), IInvokeProvider
{
    public void Invoke() => Editor.Activate(Hit);

    protected override string GetNameCore() => Text.Format(Strings.Editor_SignatureFieldName, Field.DisplayName);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
}
