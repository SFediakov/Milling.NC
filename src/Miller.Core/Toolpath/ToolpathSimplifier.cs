using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Native;

namespace Miller.Core.Toolpaths;

// Turns the cell-by-cell feed chains of the strategies into vectors (native mn_simplify): a maximal
// run of consecutive feed segments at one rate is a polyline, and Douglas-Peucker keeps only the
// vertices needed so that every dropped vertex lies within the tolerance of the chord replacing it. A
// chord is also rejected when it dips below the effective tip map (GougeChecker, same tolerance), so
// a straight line across a plateau corner is split like a curve. Douglas-Peucker splits at the
// farthest vertex, which on a straight stretch whose ends deviate is a vertex in the middle, so a
// merge pass then drops every kept vertex whose neighbours' chord still holds all vertices between
// them. A flat or constant-slope row therefore becomes one segment, a circle becomes chords whose
// sagitta is the tolerance, and the first and last point of every run, every rapid and every plunge
// stay exactly where the strategy put them.
public static class ToolpathSimplifier
{
    public static unsafe Toolpath Simplify(Toolpath toolpath, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        if (!(tolerance >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be zero or positive.");
        }

        var segments = CoreNative.SegmentsOf(toolpath);
        var grid = CoreNative.GridOf(effectiveTip);
        CoreNative.Segment* result = null;
        int count;
        fixed (CoreNative.Segment* s = segments)
        fixed (float* t = effectiveTip.Z)
        {
            CoreNative.Check(CoreNative.mn_simplify(s, segments.Length, &grid, t, tolerance, &result, &count));
        }

        try
        {
            return CoreNative.ToolpathOf(result, count);
        }
        finally
        {
            CoreNative.mn_free(result);
        }
    }

    // Indices of the vertices kept by Douglas-Peucker, ascending, always including 0 and the last.
    public static unsafe List<int> KeptIndices(IReadOnlyList<Vector3> points, HeightMap effectiveTip, float tolerance, float feedRate)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var flat = new float[Math.Max(points.Count * 3, 1)];
        for (var k = 0; k < points.Count; k++)
        {
            flat[3 * k] = points[k].X;
            flat[3 * k + 1] = points[k].Y;
            flat[3 * k + 2] = points[k].Z;
        }

        var grid = CoreNative.GridOf(effectiveTip);
        int* kept = null;
        int count;
        fixed (float* p = flat, t = effectiveTip.Z)
        {
            CoreNative.Check(CoreNative.mn_kept_indices(p, points.Count, &grid, t, tolerance, feedRate, &kept, &count));
        }

        try
        {
            return new List<int>(new ReadOnlySpan<int>(kept, count).ToArray());
        }
        finally
        {
            CoreNative.mn_free(kept);
        }
    }

    public static unsafe float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var point = stackalloc float[] { p.X, p.Y, p.Z };
        var from = stackalloc float[] { a.X, a.Y, a.Z };
        var to = stackalloc float[] { b.X, b.Y, b.Z };
        return CoreNative.mn_distance_to_segment(point, from, to);
    }
}
