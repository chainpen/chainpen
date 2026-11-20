using System.Collections.Immutable;
using Fillsign.Core.Model;
using Fillsign.Pdf.Export;
using Fillsign.Pdf.Import;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Cli;

/// <summary>Something the person asked for can't be done; the message says why and what to do.</summary>
public sealed class FormException(string message) : Exception(message);

/// <summary>A form field as the command line and MCP show it: one entry per field, with a radio group's answers as its options.</summary>
/// <param name="Kind">text, checkbox, radio, choice, multichoice or signature.</param>
/// <param name="Value">The text or chosen option; "Yes"/"Off" style values for check boxes are given as <paramref name="IsChecked"/>.</param>
public sealed record FieldInfo(
    string Name,
    string Label,
    string Kind,
    int Page,
    string Value,
    bool? IsChecked,
    ImmutableArray<string> Options,
    ImmutableArray<string> Chosen,
    bool CanFill,
    int MaxLength,
    bool AcceptsOtherAnswers);

/// <summary>What a file is, before anything is done to it.</summary>
/// <param name="FillableFields">Fields this command can fill.</param>
/// <param name="SignatureFields">Fields to sign in the app. With <paramref name="FillableFields"/>, the count the app gives.</param>
public sealed record DocumentInfo(string Path, int Pages, bool IsWorkingCopy, int FillableFields, int SignatureFields, bool OwnerForbidsChanges);

/// <summary>
/// One PDF opened for scripting (FS-969, FS-970): its form fields, filling them, and writing a final copy or a working
/// copy, the same way the app does. It never signs, and refuses files whose owner forbids changes unless the caller
/// says to go ahead (FS-018).
/// </summary>
public sealed class FormFile : IDisposable
{
    private readonly SourceDocument _source;
    private readonly SourcePage[] _pages;

    private FormFile(string path, SourceDocument source, DocumentState state, bool isWorkingCopy, bool forbidsChanges)
    {
        Path = path;
        _source = source;
        State = state;
        IsWorkingCopy = isWorkingCopy;
        OwnerForbidsChanges = forbidsChanges;
        _source.LoadPages(null, CancellationToken.None);
        _pages = [.. Enumerable.Range(0, _source.PageCount).Select(_source.GetPage)];
    }

    public string Path { get; }

    /// <summary>The document as Fillsign would show it: a working copy's own edits included.</summary>
    public DocumentState State { get; private set; }

    public bool IsWorkingCopy { get; }

    public bool OwnerForbidsChanges { get; }

    public static async Task<FormFile> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FormException($"There's no file at {full}.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        OpenResult result;
        try
        {
            result = await DocumentLoader.OpenAsync(
                [new InputFile(System.IO.Path.GetFileName(full), full, bytes)],
                (_, _) => Task.FromResult<string?>(null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FormException($"{System.IO.Path.GetFileName(full)} needs a password. Open it in Fillsign instead.");
        }
        catch (DocumentOpenException ex)
        {
            throw new FormException(string.Join(" ", ex.Problems.Select(problem => problem.Message)));
        }

        bool isWorkingCopy = result.Restore is { Kind: RestoreKind.Restored };
        DocumentState state = isWorkingCopy ? result.Restore!.Document : DocumentState.ForSourcePages(result.Source.PageCount);
        return new FormFile(full, result.Source, state, isWorkingCopy, !result.RestrictedFiles.IsEmpty);
    }

    public DocumentInfo Info()
    {
        IReadOnlyList<FieldInfo> fields = Fields();
        return new(Path, State.Pages.Length, IsWorkingCopy, fields.Count(field => field.CanFill), fields.Count(field => field.Kind == "signature"), OwnerForbidsChanges);
    }

    /// <summary>The fillable fields, page by page in the form's order; buttons left out, signature fields listed but not fillable.</summary>
    public IReadOnlyList<FieldInfo> Fields()
    {
        var fields = new List<FieldInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < State.Pages.Length; index++)
        {
            IReadOnlyList<FormField> widgets = _source.FormFields(State.Pages[index].SourceIndex);
            foreach (FormField field in widgets)
            {
                if (field.Kind == FormFieldKind.PushButton || !seen.Add(field.Name))
                {
                    continue;
                }

                FormField[] group = [.. widgets.Where(other => other.Name == field.Name)];
                fields.Add(Describe(field, group, index + 1));
            }
        }

        return fields;
    }

    private FieldInfo Describe(FormField field, FormField[] group, int page)
    {
        string value = State.Fields.TryGetValue(field.Name, out string? filled) ? filled : field.Value;
        bool canFill = !field.IsReadOnly && field.Kind != FormFieldKind.Signature;
        return field.Kind switch
        {
            FormFieldKind.CheckBox => new FieldInfo(field.Name, field.DisplayName, "checkbox", page, string.Empty, value != PdfiumDocument.FieldOff, [], [], canFill, 0, false),
            FormFieldKind.Radio => new FieldInfo(field.Name, field.DisplayName, "radio", page, value == PdfiumDocument.FieldOff ? string.Empty : value, null,
                [.. group.Select(button => button.OnValue).Where(option => option.Length > 0).Distinct()], [], canFill, 0, false),
            FormFieldKind.Choice when field.IsMultiSelect => new FieldInfo(field.Name, field.DisplayName, "multichoice", page, string.Empty, null,
                field.Options, [.. field.Options.Where(PdfiumDocument.SplitChoices(value).Contains)], canFill, 0, false),
            FormFieldKind.Choice => new FieldInfo(field.Name, field.DisplayName, "choice", page, value, null, field.Options, [], canFill, 0, field.IsEditable),
            FormFieldKind.Signature => new FieldInfo(field.Name, field.DisplayName, "signature", page, string.Empty, null, [], [], false, 0, false),
            _ => new FieldInfo(field.Name, field.DisplayName, "text", page, value, null, [], [], canFill, field.MaxLength, false),
        };
    }

    /// <summary>
    /// Fills fields by name. Each value is checked against the field: text within its limit, a check box's yes or no,
    /// one of a radio group's or list's options (or any answer where a drop-down takes one), and for lists that allow
    /// several answers, the options to choose. Nothing is changed unless every value is right.
    /// </summary>
    /// <param name="values">Field name to its values: one value, or several for a multi-select list.</param>
    public void Fill(IReadOnlyDictionary<string, IReadOnlyList<string>> values, bool editAnyway = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (OwnerForbidsChanges && !editAnyway)
        {
            throw new FormException("The file's owner has set it so it can't be changed. Fillsign leaves it as it is unless you say to edit it anyway.");
        }

        Dictionary<string, FieldInfo> fields = Fields().ToDictionary(field => field.Name, StringComparer.Ordinal);
        DocumentState state = State;
        var problems = new List<string>();
        foreach ((string name, IReadOnlyList<string> given) in values)
        {
            if (!fields.TryGetValue(name, out FieldInfo? field))
            {
                problems.Add($"There's no field called “{name}”. Run `fillsign fields` to list them.");
                continue;
            }

            if (!field.CanFill)
            {
                problems.Add(field.Kind == "signature"
                    ? $"“{name}” is a signature field. Sign it in Fillsign: signing isn't done for scripts or assistants."
                    : $"“{name}” can't be changed: the form fills it in itself.");
                continue;
            }

            if (ValueFor(field, given, out string? problem) is { } value)
            {
                state = state.WithField(name, value);
            }
            else
            {
                problems.Add(problem!);
            }
        }

        if (problems.Count > 0)
        {
            throw new FormException(string.Join(Environment.NewLine, problems));
        }

        State = state;
    }

    private static string? ValueFor(FieldInfo field, IReadOnlyList<string> given, out string? problem)
    {
        problem = null;
        string first = given.Count > 0 ? given[0] : string.Empty;
        string? Option(IEnumerable<string> options) => options.FirstOrDefault(option => string.Equals(option, first, StringComparison.OrdinalIgnoreCase));
        switch (field.Kind)
        {
            case "checkbox":
                bool? ticked = first.ToUpperInvariant() switch
                {
                    "TRUE" or "YES" or "ON" or "1" or "X" => true,
                    "FALSE" or "NO" or "OFF" or "0" or "" => false,
                    _ => null,
                };
                if (ticked is null)
                {
                    problem = $"“{field.Name}” is a check box: give yes or no.";
                    return null;
                }

                return ticked.Value ? "Yes" : PdfiumDocument.FieldOff;
            case "radio":
                if (Option(field.Options) is { } answer)
                {
                    return answer;
                }

                problem = $"“{field.Name}” takes one of: {string.Join(", ", field.Options)}.";
                return null;
            case "choice":
                if (Option(field.Options) is { } choice)
                {
                    return choice;
                }

                if (field.AcceptsOtherAnswers)
                {
                    return first;
                }

                problem = $"“{field.Name}” takes one of: {string.Join(", ", field.Options.Where(option => option.Length > 0))}.";
                return null;
            case "multichoice":
                var chosen = new List<string>();
                foreach (string one in given.Where(value => value.Length > 0))
                {
                    if (field.Options.FirstOrDefault(option => string.Equals(option, one, StringComparison.OrdinalIgnoreCase)) is { } match)
                    {
                        chosen.Add(match);
                    }
                    else
                    {
                        problem = $"“{field.Name}” takes any of: {string.Join(", ", field.Options)}.";
                        return null;
                    }
                }

                return PdfiumDocument.JoinChoices(field.Options.Where(chosen.Contains));
            default:
                if (field.MaxLength > 0 && first.Length > field.MaxLength)
                {
                    problem = $"“{field.Name}” takes up to {field.MaxLength} characters; “{first}” has {first.Length}.";
                    return null;
                }

                if (first.Length > DocumentState.MaxFieldValueLength)
                {
                    problem = $"“{field.Name}” is too long.";
                    return null;
                }

                return first;
        }
    }

    /// <summary>The final copy to send, optionally with its fields locked, as the app's Export does.</summary>
    public void WriteFinalCopy(string outputPath, bool lockFields, bool overwrite)
    {
        byte[] final = DocumentWriter.FinalCopy(CleanSource(), _pages, State).Pdf;
        Write(outputPath, lockFields ? DocumentWriter.WithFormFieldsLocked(final) : final, overwrite);
    }

    /// <summary>A working copy: opens in Fillsign with everything still editable.</summary>
    public void WriteWorkingCopy(string outputPath, bool overwrite) =>
        Write(outputPath, DocumentWriter.WorkingCopy(CleanSource(), _pages, State).Pdf, overwrite);

    private byte[] CleanSource() => PdfCompactor.Compact(_source.Pdf.SaveCopy(removeSecurity: true));

    /// <summary>Writes through a temporary file beside the target, so a failed write never leaves half a PDF.</summary>
    private static void Write(string outputPath, byte[] pdf, bool overwrite)
    {
        string full = System.IO.Path.GetFullPath(outputPath);
        if (File.Exists(full) && !overwrite)
        {
            throw new FormException($"{full} already exists. Choose another name, or say to overwrite it.");
        }

        string? folder = System.IO.Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new FormException($"There's no folder {folder}.");
        }

        string temporary = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, pdf);
            File.Move(temporary, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            throw new FormException($"Fillsign couldn’t write {full}: {ex.Message}");
        }
    }

    public void Dispose() => _source.Dispose();
}
