using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Chainpen.App.Rendering;
using Chainpen.App.ViewModels;

namespace Chainpen.App.Controls;

/// <summary>
/// A page's thumbnail in the Pages panel (FS-206). Rendered only while it's realised in the virtualised list:
/// it registers its request with <see cref="ThumbnailRequests"/> when shown and withdraws it when recycled.
/// </summary>
public sealed class PageThumbnail : Control
{
    public static readonly StyledProperty<ThumbnailRequests?> RequestsProperty =
        AvaloniaProperty.Register<PageThumbnail, ThumbnailRequests?>(nameof(Requests));

    private static readonly BoxShadows s_shadow = BoxShadows.Parse("0 1 2 0 #2e000000");
    private PageViewModel? _page;
    private bool _isAttached;

    static PageThumbnail()
    {
        RequestsProperty.Changed.AddClassHandler<PageThumbnail>((thumbnail, _) => thumbnail.Update());
    }

    public ThumbnailRequests? Requests
    {
        get => GetValue(RequestsProperty);
        set => SetValue(RequestsProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        Requests?.Withdraw(this);
        Requests?.PageRendered -= OnRendered;
    }

    public override void Render(DrawingContext context)
    {
        if (_page is null || Requests is null)
        {
            return;
        }

        Rect rect = PageRect(_page);
        if (_page.IsDamaged)
        {
            context.FillRectangle(Brush("BorderSubtle"), rect);
            return;
        }

        context.DrawRectangle(Brush("Page"), null, new RoundedRect(rect), s_shadow);
        if (!_page.IsLoaded)
        {
            return;
        }

        RenderedPage? image = Requests.Renderer.TryGet(_page.Entry.SourceIndex, _page.Entry.Rotation, Scale(_page));
        if (image is not null)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(image.Bitmap, image.SourceRect, rect);
            }
        }
    }

    private void Update()
    {
        ThumbnailRequests? requests = Requests;
        requests?.PageRendered -= OnRendered;
        _page = DataContext as PageViewModel;
        if (requests is null)
        {
            return;
        }

        requests.PageRendered += OnRendered;
        if (_page is { IsLoaded: true, IsDamaged: false } page && _isAttached)
        {
            requests.Want(this, new RenderRequest(page.Entry.SourceIndex, page.Entry.Rotation, Scale(page)));
        }
        else
        {
            requests.Withdraw(this);
        }

        InvalidateVisual();
    }

    private void OnRendered(int sourceIndex)
    {
        if (_page?.Entry.SourceIndex == sourceIndex)
        {
            InvalidateVisual();
        }
    }

    /// <summary>The page fitted and centred in the thumbnail's box.</summary>
    private Rect PageRect(PageViewModel page)
    {
        double fit = Math.Min(Bounds.Width / page.DisplaySize.Width, Bounds.Height / page.DisplaySize.Height);
        double w = page.DisplaySize.Width * fit, h = page.DisplaySize.Height * fit;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private double Scale(PageViewModel page)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double width = Bounds.Width > 0 ? Bounds.Width : 104;
        double height = Bounds.Height > 0 ? Bounds.Height : 138;
        return Math.Round(Math.Min(width / page.DisplaySize.Width, height / page.DisplaySize.Height) * scaling, 3);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Gray;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Update();
    }
}
