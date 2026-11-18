using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Fillsign.Core.Model;
using Fillsign.Pdf.Import;
using Fillsign.Pdf.Pdfium;

namespace Fillsign.App.Rendering;

/// <summary>Which list a request belongs to. The main view always renders before thumbnails.</summary>
public enum RenderLane
{
    View,
    Thumbnails,
}

/// <summary>A page image wanted at a size: source page, the rotation it's shown with, and pixels per point.</summary>
public readonly record struct RenderRequest(int SourceIndex, Rotation Rotation, double Scale);

/// <summary>A rendered page: draw <see cref="SourceRect"/> of <see cref="Bitmap"/>.</summary>
public sealed record RenderedPage(WriteableBitmap Bitmap, Rect SourceRect, double Scale);

/// <summary>
/// Renders pages on one dedicated thread (PDFium isn't thread-safe) and keeps them in a memory-limited cache
/// (FS-203). Each lane's wish list is replaced on every scroll or zoom, so stale requests are dropped rather than
/// queued. Bitmaps are pooled by size bucket because each new WriteableBitmap allocation forces a full GC
/// (Phase 0 finding); pages are rendered into the top-left of a slightly larger pooled bitmap.
/// </summary>
public sealed class PageRenderService : IDisposable
{
    private const long CacheLimitBytes = 384L * 1024 * 1024;
    private const long PoolLimitBytes = 64L * 1024 * 1024;

    // Bitmap sizes round up to this many pixels, so nearby zoom levels reuse the same pooled bitmaps.
    private const int SizeBucket = 64;

    // Pages are never rendered larger than this, whatever the zoom (memory and render time).
    private const double MaxPixels = 24_000_000;

    private readonly SourceDocument _source;
    private readonly Thread _thread;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _lock = new();
    private readonly Dictionary<CacheKey, CacheEntry> _cache = [];
    private readonly Dictionary<PixelSize, Stack<WriteableBitmap>> _pool = [];
    private readonly Dictionary<RenderLane, List<RenderRequest>> _wanted = new() { [RenderLane.View] = [], [RenderLane.Thumbnails] = [] };
    private long _cacheBytes;
    private long _poolBytes;
    private long _useCounter;
    private volatile bool _isStopping;

    public PageRenderService(SourceDocument source)
    {
        _source = source;
        _thread = new Thread(Run) { IsBackground = true, Name = "PDFium render" };
        _thread.Start();
    }

    /// <summary>Raised on the UI thread when a page image is ready.</summary>
    public event Action<int>? PageRendered;

    /// <summary>Raised on the UI thread when PDFium can't render a source page (FS-209).</summary>
    public event Action<int>? PageFailed;

    /// <summary>The page size to render for a scale, capped at <see cref="MaxPixels"/>.</summary>
    public static PixelSize PixelSizeFor(PageSize displaySize, double scale)
    {
        double pixels = displaySize.Width * displaySize.Height * scale * scale;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
        }

        return new PixelSize(Math.Max(1, (int)Math.Round(displaySize.Width * scale)), Math.Max(1, (int)Math.Round(displaySize.Height * scale)));
    }

    /// <summary>
    /// The sharpest cached image of a page in this rotation: the exact scale if it's ready, otherwise the closest
    /// one, which the caller stretches until the sharp one arrives (FR-2.4). Call on the UI thread.
    /// </summary>
    public RenderedPage? TryGet(int sourceIndex, Rotation rotation, double scale)
    {
        lock (_lock)
        {
            if (_isStopping)
            {
                return null;
            }

            CacheEntry? best = null;
            foreach (var (key, entry) in _cache)
            {
                if (key.SourceIndex != sourceIndex || key.QuarterTurns != rotation.QuarterTurns)
                {
                    continue;
                }

                if (best is null || Math.Abs(Math.Log(entry.Scale / scale)) < Math.Abs(Math.Log(best.Scale / scale)))
                {
                    best = entry;
                }
            }

            if (best is null)
            {
                return null;
            }

            best.LastUsed = ++_useCounter;
            return new RenderedPage(best.Bitmap, new Rect(0, 0, best.PixelSize.Width, best.PixelSize.Height), best.Scale);
        }
    }

    public bool Has(RenderRequest request)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(CacheKey.For(request));
        }
    }

    /// <summary>Replaces a lane's outstanding requests. Order is priority: visible pages first.</summary>
    public void Request(RenderLane lane, IEnumerable<RenderRequest> wanted)
    {
        // Layout callbacks queued before the window closed can still arrive; ignore them.
        if (_isStopping)
        {
            return;
        }

        lock (_lock)
        {
            _wanted[lane] = [.. wanted.Where(request => !_cache.ContainsKey(CacheKey.For(request)))];
        }

        _wake.Release();
    }

    public void Dispose()
    {
        _isStopping = true;
        _wake.Release();
        _thread.Join();
        _wake.Dispose();
        lock (_lock)
        {
            foreach (CacheEntry entry in _cache.Values)
            {
                entry.Bitmap.Dispose();
            }

            foreach (WriteableBitmap bitmap in _pool.Values.SelectMany(stack => stack))
            {
                bitmap.Dispose();
            }

            _cache.Clear();
            _pool.Clear();
        }
    }

    private void Run()
    {
        while (true)
        {
            _wake.Wait();
            while (!_isStopping && TryTakeNext(out RenderRequest request))
            {
                RenderOne(request);
            }

            if (_isStopping)
            {
                return;
            }
        }
    }

    private bool TryTakeNext(out RenderRequest request)
    {
        lock (_lock)
        {
            foreach (RenderLane lane in (RenderLane[])[RenderLane.View, RenderLane.Thumbnails])
            {
                List<RenderRequest> list = _wanted[lane];
                while (list.Count > 0)
                {
                    request = list[0];
                    list.RemoveAt(0);
                    if (!_cache.ContainsKey(CacheKey.For(request)))
                    {
                        return true;
                    }
                }
            }

            request = default;
            return false;
        }
    }

    private void RenderOne(RenderRequest request)
    {
        if (request.SourceIndex >= _source.PageCount)
        {
            return;
        }

        SourcePage page = _source.GetPage(request.SourceIndex);
        PageSize displaySize = page.DisplaySize.Turned(request.Rotation);
        PixelSize pixels = PixelSizeFor(displaySize, request.Scale);
        WriteableBitmap bitmap = Rent(pixels);
        try
        {
            using ILockedFramebuffer buffer = bitmap.Lock();
            unsafe
            {
                var span = new Span<byte>((void*)buffer.Address, buffer.RowBytes * pixels.Height);
                _source.Pdf.RenderPage(request.SourceIndex, span, pixels.Width, pixels.Height, buffer.RowBytes, request.Rotation.QuarterTurns);
            }
        }
        catch (PdfPageException)
        {
            Return(bitmap);
            Dispatcher.UIThread.Post(() => PageFailed?.Invoke(request.SourceIndex));
            return;
        }

        lock (_lock)
        {
            var key = CacheKey.For(request);
            Remove(key);
            _cache[key] = new CacheEntry(bitmap, pixels, request.Scale) { LastUsed = ++_useCounter };
            _cacheBytes += Bytes(bitmap);
            Evict();
        }

        Dispatcher.UIThread.Post(() => PageRendered?.Invoke(request.SourceIndex), DispatcherPriority.Render);
    }

    /// <summary>Removes least-recently-drawn images that nobody currently wants, until under the limit.</summary>
    private void Evict()
    {
        if (_cacheBytes <= CacheLimitBytes)
        {
            return;
        }

        var wanted = _wanted.Values.SelectMany(list => list).Select(CacheKey.For).ToHashSet();
        foreach (var (key, _) in _cache.OrderBy(pair => pair.Value.LastUsed).ToList())
        {
            if (_cacheBytes <= CacheLimitBytes)
            {
                return;
            }

            if (!wanted.Contains(key))
            {
                Remove(key);
            }
        }
    }

    private void Remove(CacheKey key)
    {
        if (_cache.Remove(key, out CacheEntry? entry))
        {
            _cacheBytes -= Bytes(entry.Bitmap);
            Return(entry.Bitmap);
        }
    }

    private WriteableBitmap Rent(PixelSize pixels)
    {
        var bucket = new PixelSize(RoundUp(pixels.Width), RoundUp(pixels.Height));
        lock (_lock)
        {
            if (_pool.TryGetValue(bucket, out var stack) && stack.TryPop(out WriteableBitmap? pooled))
            {
                _poolBytes -= Bytes(pooled);
                return pooled;
            }
        }

        return new WriteableBitmap(bucket, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    private void Return(WriteableBitmap bitmap)
    {
        lock (_lock)
        {
            if (_poolBytes + Bytes(bitmap) > PoolLimitBytes)
            {
                // Dispose where drawing happens; Avalonia keeps its own reference while a frame still uses it.
                Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
                return;
            }

            if (!_pool.TryGetValue(bitmap.PixelSize, out var stack))
            {
                stack = new Stack<WriteableBitmap>();
                _pool[bitmap.PixelSize] = stack;
            }

            stack.Push(bitmap);
            _poolBytes += Bytes(bitmap);
        }
    }

    private static int RoundUp(int value) => ((value + SizeBucket - 1) / SizeBucket) * SizeBucket;

    private static long Bytes(WriteableBitmap bitmap) => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    private readonly record struct CacheKey(int SourceIndex, int QuarterTurns, int ScaleKey)
    {
        // Scales within 0.1% share a cache entry.
        public static CacheKey For(RenderRequest request) =>
            new(request.SourceIndex, request.Rotation.QuarterTurns, (int)Math.Round(request.Scale * 1000));
    }

    private sealed record CacheEntry(WriteableBitmap Bitmap, PixelSize PixelSize, double Scale)
    {
        public long LastUsed { get; set; }
    }
}
