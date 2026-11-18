using Fillsign.App.Controls;

namespace Fillsign.App.Rendering;

/// <summary>Collects the requests of the thumbnails currently shown and passes them to the renderer as one lane.</summary>
public sealed class ThumbnailRequests(PageRenderService renderer)
{
    private readonly Dictionary<PageThumbnail, RenderRequest> _wanted = [];

    public PageRenderService Renderer { get; } = renderer;

    public event Action<int>? PageRendered
    {
        add => Renderer.PageRendered += value;
        remove => Renderer.PageRendered -= value;
    }

    public void Want(PageThumbnail thumbnail, RenderRequest request)
    {
        if (_wanted.TryGetValue(thumbnail, out RenderRequest existing) && existing == request)
        {
            return;
        }

        _wanted[thumbnail] = request;
        Send();
    }

    public void Withdraw(PageThumbnail thumbnail)
    {
        if (_wanted.Remove(thumbnail))
        {
            Send();
        }
    }

    private void Send() => Renderer.Request(RenderLane.Thumbnails, _wanted.Values.Distinct().OrderBy(request => request.SourceIndex));
}
