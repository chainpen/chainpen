using System.Collections.Immutable;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Import;

/// <summary>A file the person chose or dropped.</summary>
public sealed record InputFile(string Name, string? Path, byte[]? Bytes, Exception? ReadError = null)
{
    /// <summary>Reads a file from disk. A read failure is kept and reported as <see cref="OpenProblemKind.CantRead"/>.</summary>
    public static async Task<InputFile> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string name = System.IO.Path.GetFileName(path);
        try
        {
            return new InputFile(name, path, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InputFile(name, path, null, ex);
        }
    }
}

/// <summary>
/// Asks the person for a file's password (D12). <paramref name="wasWrong"/> is true after a wrong try. Return null to
/// skip the file. The password is never stored.
/// </summary>
public delegate Task<string?> PasswordPrompt(string fileName, bool wasWrong);

/// <param name="Skipped">Files left out, with the reason ("2 of 3 files were opened…"). Files skipped at the password
/// prompt aren't listed: that was the person's choice.</param>
/// <param name="Restore">The working copy's editable record when one was found; null for an ordinary PDF.</param>
public sealed record OpenResult(SourceDocument Source, ImmutableArray<OpenProblem> Skipped, WorkingCopyRestore? Restore = null)
{
    /// <summary>Files whose owner protected them against changes (FS-214): the person is asked how to go on.</summary>
    public ImmutableArray<string> RestrictedFiles { get; init; } = [];
}

/// <summary>Opens PDFs and images as one document (FR-1.1–1.3, 1.6).</summary>
public static class DocumentLoader
{
    /// <summary>
    /// Opens the files as one document, in the order given. A single PDF is used as it is; several files, or any
    /// image, are combined into a new in-memory PDF. The files on disk are never changed (FR-1.4).
    /// </summary>
    /// <exception cref="DocumentOpenException">Nothing could be opened.</exception>
    /// <exception cref="OperationCanceledException">Every file was skipped at the password prompt, or the open was cancelled.</exception>
    /// <param name="restoreRecord">False opens a working copy as other readers show it ("Open final copy instead").</param>
    public static async Task<OpenResult> OpenAsync(IReadOnlyList<InputFile> files, PasswordPrompt askPassword, CancellationToken cancellationToken, bool restoreRecord = true)
    {
        ArgumentNullException.ThrowIfNull(files);
        var (parts, problems) = await OpenPartsAsync(files, askPassword, cancellationToken).ConfigureAwait(false);
        if (parts.Count == 0)
        {
            return problems.Count > 0 ? throw new DocumentOpenException(problems) : throw new OperationCanceledException();
        }

        ImmutableArray<string> restricted = [.. parts.Where(part => part.ForbidsChanges).Select(part => part.File.Name)];
        if (parts is [{ IsImage: false } only])
        {
            // Editable records are restored only when a single document is opened (FR-9.7).
            WorkingCopyRestore? restore = files.Count == 1 && restoreRecord ? await Task.Run(() => FindRecord(only), cancellationToken).ConfigureAwait(false) : null;
            if (restore is { Kind: RestoreKind.Restored })
            {
                only.Pdf.Dispose();
                PdfiumDocument clean = await Task.Run(() => PdfiumDocument.Open(restore.CleanSource), cancellationToken).ConfigureAwait(false);
                return new OpenResult(new SourceDocument(only.File.Name, only.File.Path, clean, only.NeedsPassword), [.. problems], restore) { RestrictedFiles = restricted };
            }

            return new OpenResult(new SourceDocument(only.File.Name, only.File.Path, only.Pdf, only.NeedsPassword), [.. problems], restore) { RestrictedFiles = restricted };
        }

        PdfiumDocument combined = await Task.Run(() => Combine(parts), cancellationToken).ConfigureAwait(false);
        return new OpenResult(new SourceDocument(parts[0].File.Name, null, combined, needsPassword: false), [.. problems]) { RestrictedFiles = restricted };
    }

    /// <summary>
    /// Opens files to add to an already-open document (FR-6.8): returns one in-memory PDF of all their pages, which
    /// the caller imports with <see cref="SourceDocument.AppendFrom"/> and then disposes.
    /// </summary>
    public static async Task<(PdfiumDocument Pages, ImmutableArray<OpenProblem> Skipped, ImmutableArray<string> Restricted)> OpenForAddingAsync(
        IReadOnlyList<InputFile> files,
        PasswordPrompt askPassword,
        CancellationToken cancellationToken)
    {
        var (parts, problems) = await OpenPartsAsync(files, askPassword, cancellationToken).ConfigureAwait(false);
        if (parts.Count == 0)
        {
            return problems.Count > 0 ? throw new DocumentOpenException(problems) : throw new OperationCanceledException();
        }

        ImmutableArray<string> restricted = [.. parts.Where(part => part.ForbidsChanges).Select(part => part.File.Name)];
        PdfiumDocument combined = await Task.Run(() => Combine(parts), cancellationToken).ConfigureAwait(false);
        return (combined, [.. problems], restricted);
    }

    /// <summary>
    /// Opens a clean original kept in a working copy, to restore Fillsign edits the person chose to bring back after
    /// another app changed the file (FR-9.6).
    /// </summary>
    public static SourceDocument OpenCleanSource(string name, string? path, byte[] cleanSource) =>
        new(name, path, PdfiumDocument.Open(cleanSource), needsPassword: false);

    /// <summary>
    /// Looks for Fillsign's editable record. A damaged record, or one that doesn't fit its clean original, is ignored
    /// and the file opens as an ordinary PDF (FR-9.5).
    /// </summary>
    private static WorkingCopyRestore? FindRecord(Part part)
    {
        if (part.File.Bytes is not { } bytes)
        {
            return null;
        }

        Core.Session.SessionLookup lookup = Core.Session.SessionStore.Find(bytes);
        if (lookup is not { Record: { } record, IsOriginalAvailable: true } || lookup.Status is not (Core.Session.SessionStatus.Intact or Core.Session.SessionStatus.ChangedElsewhere))
        {
            if (lookup.Problem is not null)
            {
                System.Diagnostics.Trace.TraceWarning($"{part.File.Name}: the Fillsign record was ignored. {lookup.Problem}");
            }

            return null;
        }

        byte[] clean = bytes[..(int)record.Base.Length];
        try
        {
            using PdfiumDocument check = PdfiumDocument.Open(clean);
            if (check.PageCount != record.SourcePageCount)
            {
                System.Diagnostics.Trace.TraceWarning($"{part.File.Name}: the Fillsign record doesn't match its original's pages, so it was ignored.");
                return null;
            }
        }
        catch (PdfOpenException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"{part.File.Name}: the clean original in the working copy can't be opened, so the record was ignored. {ex.Message}");
            return null;
        }

        DateTimeOffset? savedAt = part.File.Path is { } path && File.Exists(path) ? File.GetLastWriteTime(path) : null;
        RestoreKind kind = lookup.Status == Core.Session.SessionStatus.Intact ? RestoreKind.Restored : RestoreKind.ChangedElsewhere;
        return new WorkingCopyRestore(kind, record.Document, clean, savedAt);
    }

    /// <summary>Whether the bytes start like a PDF ("%PDF" in the first KB, as readers accept).</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 1024)].IndexOf("%PDF"u8) >= 0;

    private static async Task<(List<Part> Parts, List<OpenProblem> Problems)> OpenPartsAsync(
        IReadOnlyList<InputFile> files,
        PasswordPrompt askPassword,
        CancellationToken cancellationToken)
    {
        var parts = new List<Part>();
        var problems = new List<OpenProblem>();
        try
        {
            foreach (InputFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Bytes is not { } bytes)
                {
                    problems.Add(new OpenProblem(file.Name, OpenProblemKind.CantRead));
                    continue;
                }

                if (LooksLikePdf(bytes))
                {
                    Part? part = await OpenPdfAsync(file, bytes, askPassword, problems, cancellationToken).ConfigureAwait(false);
                    if (part is not null)
                    {
                        parts.Add(part);
                    }

                    continue;
                }

                Part? image = await Task.Run(() => OpenImage(file, bytes), cancellationToken).ConfigureAwait(false);
                if (image is null)
                {
                    problems.Add(new OpenProblem(file.Name, OpenProblemKind.NotPdfOrImage));
                }
                else
                {
                    parts.Add(image);
                }
            }
        }
        catch
        {
            parts.ForEach(part => part.Pdf.Dispose());
            throw;
        }

        return (parts, problems);
    }

    private static async Task<Part?> OpenPdfAsync(InputFile file, byte[] bytes, PasswordPrompt askPassword, List<OpenProblem> problems, CancellationToken cancellationToken)
    {
        string? password = null;
        bool wasWrong = false;
        while (true)
        {
            try
            {
                PdfiumDocument pdf = await Task.Run(() => PdfiumDocument.Open(bytes, password), cancellationToken).ConfigureAwait(false);
                if (pdf.PageCount == 0)
                {
                    pdf.Dispose();
                    problems.Add(new OpenProblem(file.Name, OpenProblemKind.Damaged));
                    return null;
                }

                return new Part(file, pdf, IsImage: false, NeedsPassword: password is not null, ForbidsChanges: pdf.ForbidsChanges);
            }
            catch (PdfOpenException ex) when (ex.Error == PdfOpenError.Password)
            {
                password = await askPassword(file.Name, wasWrong).ConfigureAwait(false);
                if (password is null)
                {
                    return null;
                }

                wasWrong = true;
            }
            catch (PdfOpenException ex)
            {
                problems.Add(new OpenProblem(file.Name, ex.Error == PdfOpenError.UnsupportedSecurity ? OpenProblemKind.UnsupportedProtection : OpenProblemKind.Damaged));
                return null;
            }
        }
    }

    private static Part? OpenImage(InputFile file, byte[] bytes)
    {
        if (!ImageToPdf.IsImage(bytes))
        {
            return null;
        }

        PdfiumDocument pdf = PdfiumDocument.CreateNew();
        try
        {
            ImageToPdf.AddImagePage(pdf, bytes, 0);
            return new Part(file, pdf, IsImage: true, NeedsPassword: false);
        }
        catch (InvalidDataException)
        {
            pdf.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Puts the parts' pages into one new document. It's then replaced by a saved copy of itself, made while the
    /// parts are still open: PDFium's import leaves form widgets pointing at parent fields inside the part they came
    /// from, and following those links after the part closes would read freed memory.
    /// </summary>
    private static PdfiumDocument Combine(List<Part> parts)
    {
        var combined = PdfiumDocument.CreateNew();
        try
        {
            foreach (Part part in parts)
            {
                combined.ImportPages(part.Pdf, [.. Enumerable.Range(0, part.Pdf.PageCount)], combined.PageCount);
            }

            byte[] saved = combined.SaveCopy(removeSecurity: true);
            combined.Dispose();
            return PdfiumDocument.Open(saved);
        }
        catch
        {
            combined.Dispose();
            throw;
        }
        finally
        {
            parts.ForEach(part => part.Pdf.Dispose());
        }
    }

    private sealed record Part(InputFile File, PdfiumDocument Pdf, bool IsImage, bool NeedsPassword, bool ForbidsChanges = false);
}
