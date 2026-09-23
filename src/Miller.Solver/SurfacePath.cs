using Miller.Solver.Native;

namespace Miller.Solver;

public readonly record struct RoutePoint(float X, float Y, float Z);

// The polyline the tool follows from one point to another without dipping under the plateau of
// any cell it crosses: the tool rises in place when it starts under its own plateau, every
// crossing of a cell edge is lifted to the highest plateau touching the crossing point (never
// below the straight line between the end points), and the tool descends in place over the end
// point. Two consecutive polyline points lie in one cell at or above its plateau, so the segment
// between them does too, which is the plateau model of the gouge checker. A point on a grid line
// belongs to whichever neighbouring cell the checker assigns it to, so every cell touching the
// point counts: two at an edge, four at a corner. Computed by the native library (mn_surface_trace).
public static class SurfacePath
{
    // Appends the polyline after `a` (excluded) up to `b` (included) to `points` when it is given and
    // returns the vertical travel along the whole polyline.
    public static unsafe float Trace(RouteGrid grid, RoutePoint a, RoutePoint b, List<RoutePoint>? points)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var native = SolverNative.Grid.Of(grid);
        var from = stackalloc float[] { a.X, a.Y, a.Z };
        var to = stackalloc float[] { b.X, b.Y, b.Z };
        fixed (float* floor = grid.Floor)
        {
            if (points is null)
            {
                return SolverNative.mn_surface_trace(&native, floor, from, to, null, null);
            }

            float* list = null;
            var count = 0;
            var climb = SolverNative.mn_surface_trace(&native, floor, from, to, &list, &count);
            if (count < 0)
            {
                throw new OutOfMemoryException(SolverNative.LastError);
            }

            try
            {
                for (var k = 0; k < count; k++)
                {
                    points.Add(new RoutePoint(list[3 * k], list[3 * k + 1], list[3 * k + 2]));
                }
            }
            finally
            {
                SolverNative.mn_free(list);
            }

            return climb;
        }
    }
}
