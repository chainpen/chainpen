using Fillsign.Core.Model;

namespace Fillsign.Pdf.Import;

/// <summary>What was found when a Fillsign working copy was opened (FR-9.2–9.6).</summary>
public enum RestoreKind
{
    /// <summary>The file is as Fillsign saved it: the clean pages and every item were restored (FR-9.2).</summary>
    Restored,

    /// <summary>
    /// Another app changed the file after Fillsign saved it. It was opened as it is now; the Fillsign edits can be
    /// restored as a deliberate choice that discards the other app's changes (FR-9.6).
    /// </summary>
    ChangedElsewhere,
}

/// <summary>
/// A working copy's editable record. For <see cref="RestoreKind.Restored"/> the document already shows it; for
/// <see cref="RestoreKind.ChangedElsewhere"/>, <see cref="CleanSource"/> and <see cref="Document"/> are what restoring
/// would bring back.
/// </summary>
/// <param name="CleanSource">The clean original stored at the start of the working copy.</param>
/// <param name="SavedAt">When the working copy was last written (the file's modified time), for the notice.</param>
public sealed record WorkingCopyRestore(RestoreKind Kind, DocumentState Document, byte[] CleanSource, DateTimeOffset? SavedAt);
