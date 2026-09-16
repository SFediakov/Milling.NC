using System.Numerics;
using Miller.Core.HeightMaps;

namespace Miller.Core.Toolpaths;

// Turns the cell-by-cell feed chains of the strategies into vectors: a maximal run of consecutive
// feed segments at one rate is a polyline, and Douglas-Peucker keeps only the vertices needed so
// that every dropped vertex lies within the tolerance of the chord replacing it. A chord is also
// rejected when it dips below the effective tip map (GougeChecker, same tolerance), so a straight
// line across a plateau corner is split like a curve. A flat or constant-slope row therefore
// becomes one segment, a circle becomes chords whose sagitta is the tolerance, and the first and
// last point of every run, every rapid and every plunge stay exactly where the strategy put them.
public static class ToolpathSimplifier
{
    public static Toolpath Simplify(Toolpath toolpath, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        if (!(tolerance >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be zero or positive.");
        }

        var result = new Toolpath();
        var segments = toolpath.Segments;
        var k = 0;
        while (k < segments.Count)
        {
            var first = segments[k];
            if (first.Kind != MoveKind.Feed)
            {
                result.Add(first);
                k++;
                continue;
            }

            var end = k + 1;
            while (end < segments.Count && Continues(segments[end - 1], segments[end]))
            {
                end++;
            }

            var points = new List<Vector3>(end - k + 1) { first.Start };
            for (var n = k; n < end; n++)
            {
                points.Add(segments[n].End);
            }

            var previous = 0;
            foreach (var index in KeptIndices(points, effectiveTip, tolerance, first.FeedRate))
            {
                if (index > 0)
                {
                    result.Add(new ToolpathSegment(points[previous], points[index], MoveKind.Feed, first.FeedRate));
                    previous = index;
                }
            }

            k = end;
        }

        return result;
    }

    // Indices of the vertices kept by Douglas-Peucker, ascending, always including 0 and the last.
    public static List<int> KeptIndices(IReadOnlyList<Vector3> points, HeightMap effectiveTip, float tolerance, float feedRate)
    {
        ArgumentNullException.ThrowIfNull(points);
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2)
            {
                continue;
            }

            var split = Split(points, a, b, effectiveTip, tolerance, feedRate);
            if (split < 0)
            {
                continue;
            }

            keep[split] = true;
            stack.Push((a, split));
            stack.Push((split, b));
        }

        var kept = new List<int>();
        for (var n = 0; n < keep.Length; n++)
        {
            if (keep[n])
            {
                kept.Add(n);
            }
        }

        return kept;
    }

    // The vertex the chord a-b must keep, or -1 when the chord may replace every vertex between:
    // the farthest vertex when it is beyond the tolerance, the farthest (or the middle one of a
    // collinear stretch) when the chord itself would gouge.
    private static int Split(IReadOnlyList<Vector3> points, int a, int b, HeightMap effectiveTip, float tolerance, float feedRate)
    {
        var farthest = -1;
        var farthestDistance = -1f;
        for (var n = a + 1; n < b; n++)
        {
            var d = DistanceToSegment(points[n], points[a], points[b]);
            if (d > farthestDistance)
            {
                farthestDistance = d;
                farthest = n;
            }
        }

        if (farthestDistance > tolerance)
        {
            return farthest;
        }

        var chord = new ToolpathSegment(points[a], points[b], MoveKind.Feed, feedRate);
        if (GougeChecker.IsClear(chord, effectiveTip, tolerance))
        {
            return -1;
        }

        return farthestDistance > 0 ? farthest : (a + b) / 2;
    }

    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared == 0)
        {
            return Vector3.Distance(p, a);
        }

        var t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    private static bool Continues(ToolpathSegment previous, ToolpathSegment next)
        => next.Kind == MoveKind.Feed && next.FeedRate == previous.FeedRate && next.Start == previous.End;
}
