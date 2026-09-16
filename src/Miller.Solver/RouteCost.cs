using System.Runtime.CompilerServices;

namespace Miller.Solver;

// Travel time of a move in units of Z travel: XY moves are XySpeedFactor times faster than Z moves
// and the two are summed along the surface polyline of the move.
public static class RouteCost
{
    public const float XySpeedFactor = 3f;

    public const float ZSpeedFactor = 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Planar(in RoutePoint a, in RoutePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // Never above the exact cost: the polyline climbs at least the height difference.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LowerBound(in RoutePoint a, in RoutePoint b)
        => Planar(a, b) / XySpeedFactor + MathF.Abs(a.Z - b.Z) / ZSpeedFactor;

    public static float Exact(RouteGrid grid, in RoutePoint a, in RoutePoint b)
        => Planar(a, b) / XySpeedFactor + SurfacePath.Trace(grid, a, b, null) / ZSpeedFactor;
}
