using Chainpen.Core.Model;

namespace Chainpen.Core.Geometry;

/// <summary>
/// One step of a vector path in item-local coordinates (points, y down). The same path list is drawn on screen and
/// written into the exported PDF, so what you see is what gets exported (NFR-7).
/// </summary>
public closed record PathCommand;

public sealed record MoveTo(PagePoint To) : PathCommand;

public sealed record LineTo(PagePoint To) : PathCommand;

public sealed record QuadTo(PagePoint Control, PagePoint To) : PathCommand;

public sealed record CubicTo(PagePoint Control1, PagePoint Control2, PagePoint To) : PathCommand;

public sealed record ClosePath : PathCommand
{
    public static readonly ClosePath Instance = new();
}
