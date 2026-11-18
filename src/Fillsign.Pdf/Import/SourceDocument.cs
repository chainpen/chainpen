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

    public PdfiumDocument Pdf { get; }

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
        return [.. Enumerable.Range(first, PageCount - first)];
    }

    public void Dispose() => Pdf.Dispose();
}
