using Miller.Solver.Native;

namespace Miller.Solver;

// Travel time of a move in units of Z travel: XY moves are XySpeedFactor times faster than Z moves
// and the two are summed along the surface polyline of the move (native library).
public static class RouteCost
{
    public const float XySpeedFactor = 3f;

    public const float ZSpeedFactor = 1f;

    public static unsafe float Planar(in RoutePoint a, in RoutePoint b)
    {
        var from = stackalloc float[] { a.X, a.Y, a.Z };
        var to = stackalloc float[] { b.X, b.Y, b.Z };
        return SolverNative.mn_route_planar(from, to);
    }

    // Never above the exact cost: the polyline climbs at least the height difference.
    public static unsafe float LowerBound(in RoutePoint a, in RoutePoint b)
    {
        var from = stackalloc float[] { a.X, a.Y, a.Z };
        var to = stackalloc float[] { b.X, b.Y, b.Z };
        return SolverNative.mn_route_lower_bound(from, to);
    }

    public static unsafe float Exact(RouteGrid grid, in RoutePoint a, in RoutePoint b)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var native = SolverNative.Grid.Of(grid);
        var from = stackalloc float[] { a.X, a.Y, a.Z };
        var to = stackalloc float[] { b.X, b.Y, b.Z };
        fixed (float* floor = grid.Floor)
        {
            return SolverNative.mn_route_exact(&native, floor, from, to);
        }
    }
}
