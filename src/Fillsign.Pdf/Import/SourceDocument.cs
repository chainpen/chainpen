using System.Collections.Immutable;
using Fillsign.Core.Model;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.Pdf.Import;

/// <summary>
/// The open source PDF the editor shows. Never written to while editing: pages added from other files are imported
/// into it in memory, and the file on disk is untouched (FR-1.4). Page facts load in the background so page 1 is
/// usable straight away on long documents (FR-1.9).
/// </summary>
public sealed class SourceDocument : IDisposable
{
    // US Letter, PDFium's default when a page has no usable box.
    private static readonly SourcePage s_fallbackPage = new(new PdfBox(0, 0, 612, 792), Rotation.None);

    private readonly Lock _lock = new();
    private SourcePage?[] _pages;
    private ImmutableHashSet<int> _damaged = [];

    internal SourceDocument(string name, string? path, PdfiumDocument pdf, bool needsPassword)
    {
        Name = name;
        Path = path;
        Pdf = pdf;
        NeedsPassword = needsPassword;
        _pages = new SourcePage?[pdf.PageCount];
    }

    /// <summary>File name shown in the title strip, e.g. "consent-form.pdf".</summary>
    public string Name { get; }

    /// <summary>Where it was opened from; null when several files were combined.</summary>
    public string? Path { get; }

    /// <summary>The document as Fillsign holds it. Replaced by a saved copy of itself when pages are added (see <see cref="AppendFrom"/>).</summary>
    public PdfiumDocument Pdf { get; private set; }

    /// <summary>The file was locked with a user password (asked for on open; never kept).</summary>
    public bool NeedsPassword { get; }

    public int PageCount
    {
        get
        {
            lock (_lock)
            {
                return _pages.Length;
            }
        }
    }

    /// <summary>Pages whose facts are known; the rest show a placeholder size until loaded.</summary>
    public int LoadedPageCount
    {
        get
        {
            lock (_lock)
            {
                return _pages.Count(page => page is not null);
            }
        }
    }

    public bool IsFullyLoaded => LoadedPageCount == PageCount;

    /// <summary>Source pages PDFium can't load (FS-209: shown as a placeholder, kept or removed by the user).</summary>
    public ImmutableHashSet<int> DamagedPages
    {
        get
        {
            lock (_lock)
            {
                return _damaged;
            }
        }
    }

    /// <summary>
    /// The page's crop box and /Rotate. Until it has loaded, a guess based on the nearest loaded page, so the layout
    /// doesn't jump much when the real size arrives.
    /// </summary>
    public SourcePage GetPage(int index)
    {
        lock (_lock)
        {
            if (_pages[index] is { } page)
            {
                return page;
            }

            for (int i = index - 1; i >= 0; i--)
            {
                if (_pages[i] is { } before)
                {
                    return before;
                }
            }

            return _pages.FirstOrDefault(p => p is not null) ?? s_fallbackPage;
        }
    }

    public bool IsPageLoaded(int index)
    {
        lock (_lock)
        {
            return _pages[index] is not null;
        }
    }

    /// <summary>Loads page facts in order, reporting after each batch. Safe to call again; loaded pages are skipped.</summary>
    public void LoadPages(Action<int>? progress, CancellationToken cancellationToken)
    {
        const int BatchSize = 8;
        int count = PageCount;
        for (int start = 0; start < count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = start; i < Math.Min(count, start + BatchSize); i++)
            {
                if (IsPageLoaded(i))
                {
                    continue;
                }

                SourcePage page;
                bool isDamaged = false;
                try
                {
                    page = Pdf.GetPageInfo(i).ToSourcePage();
                }
                catch (PdfPageException)
                {
                    page = GetPage(i);
                    isDamaged = true;
                }

                lock (_lock)
                {
                    _pages[i] = page;
                    if (isDamaged)
                    {
                        _damaged = _damaged.Add(i);
                    }
                }
            }

            progress?.Invoke(Math.Min(count, start + BatchSize));
        }
    }

    /// <summary>Records a page PDFium loaded but couldn't render.</summary>
    public void MarkDamaged(int index)
    {
        lock (_lock)
        {
            _damaged = _damaged.Add(index);
        }
    }

    /// <summary>
    /// Imports pages from other files at the end of this source (FR-6.8). Returns the new source page indices, in
    /// order. Their facts are loaded before returning.
    /// </summary>
    public ImmutableArray<int> AppendFrom(PdfiumDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int first = PageCount;
        Pdf.ImportPages(other, [.. Enumerable.Range(0, other.PageCount)], first);
        lock (_lock)
        {
            Array.Resize(ref _pages, Pdf.PageCount);
        }

        LoadPages(null, CancellationToken.None);
        ReloadFromSavedCopy();
        return [.. Enumerable.Range(first, PageCount - first)];
    }

    // ----- Form fields (FS-950) -----

    private readonly Dictionary<int, IReadOnlyList<FormField>> _fields = [];
    private PdfiumDocument? _preview;
    private ImmutableSortedDictionary<string, string> _shown = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>The fillable fields on a source page as the file has them, read once.</summary>
    public IReadOnlyList<FormField> FormFields(int sourceIndex)
    {
        lock (_lock)
        {
            if (_fields.TryGetValue(sourceIndex, out IReadOnlyList<FormField>? known))
            {
                return known;
            }
        }

        IReadOnlyList<FormField> fields;
        try
        {
            fields = Pdf.GetFormFields(sourceIndex);
        }
        catch (PdfPageException)
        {
            fields = [];
        }

        lock (_lock)
        {
            _fields[sourceIndex] = fields;
        }

        return fields;
    }

    /// <summary>
    /// After pages are imported, the document is replaced by a saved copy of itself, made while the other file is
    /// still open (FS-950). PDFium's import leaves form widgets pointing at their parent field in the other file;
    /// once that closes, the form layer would follow those links into freed memory. In the saved copy every link is
    /// inside the document, and it holds exactly what saving keeps: fields made of several widgets (a radio group)
    /// lose their parent in the import, so they aren't offered as fields and can't silently lose an answer. Page
    /// facts and indexes don't change.
    /// </summary>
    private void ReloadFromSavedCopy()
    {
        PdfiumDocument reloaded = PdfiumDocument.Open(Pdf.SaveCopy(removeSecurity: true));
        lock (PdfiumLibrary.Sync)
        {
            ClosePreview();
            PdfiumDocument old = Pdf;
            Pdf = reloaded;
            old.Dispose();
        }

        lock (_lock)
        {
            _fields.Clear();
        }
    }

    /// <summary>
    /// Shows these field values on screen (FS-950). The file is never changed: values go into a private copy that pages
    /// are then rendered from, filled the same way the final copy is, so the screen matches it. Returns whether
    /// anything shown changed, so the caller knows to render the pages again.
    /// </summary>
    public bool ShowFields(ImmutableSortedDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (PdfiumLibrary.Sync)
        {
            if (fields.IsEmpty)
            {
                bool wasShowing = _preview is not null;
                ClosePreview();
                return wasShowing;
            }

            // A field set back to how the file has it (undo) needs the file's value: start again from a fresh copy.
            if (_preview is null || _shown.Keys.Any(name => !fields.ContainsKey(name)))
            {
                ClosePreview();
                _preview = PdfiumDocument.Open(Pdf.SaveCopy(removeSecurity: true));
            }

            var changed = fields.Where(pair => !_shown.TryGetValue(pair.Key, out string? shown) || shown != pair.Value)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _preview.FillFormFields(changed);
            _shown = fields;
            return changed.Count > 0;
        }
    }

    /// <summary>Renders a page with the field values being shown, if any (see <see cref="PdfiumDocument.RenderPage"/>).</summary>
    public void RenderPage(int sourceIndex, Span<byte> bgra, int pixelWidth, int pixelHeight, int stride, int extraQuarterTurns)
    {
        lock (PdfiumLibrary.Sync)
        {
            (_preview ?? Pdf).RenderPage(sourceIndex, bgra, pixelWidth, pixelHeight, stride, extraQuarterTurns);
        }
    }

    private void ClosePreview()
    {
        _preview?.Dispose();
        _preview = null;
        _shown = ImmutableSortedDictionary<string, string>.Empty;
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            ClosePreview();
        }

        Pdf.Dispose();
    }
}
