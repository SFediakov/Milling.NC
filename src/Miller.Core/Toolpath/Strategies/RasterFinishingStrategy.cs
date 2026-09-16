using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths.Strategies;

// Parallel rows along X at FinishingStepover following the effective tip map. A row runs from the
// first cell center through the cell boundaries, each at the higher of the two tips it separates, to
// the last cell center: every point is at or above the plateau of the cell it is in, so the path is
// safe under the plateau model of GougeChecker, and a constant slope gives collinear boundary points
// that merge into one segment. NaN cells and cells outside the finishing mask break a row into runs.
// Zigzag joins consecutive rows with a feed when that feed is clear.
public sealed class RasterFinishingStrategy : IToolpathStrategy
{
    public const string StrategyId = "raster-finishing";

    // Sine of the angle below which two consecutive segments count as one straight line.
    public const float MergeTolerance = 1e-4f;

    public string Id => StrategyId;

    public string DisplayName => "Raster finishing (parallel rows)";

    public MillingOperation Operation => MillingOperation.Finishing;

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var mask = context.Plan.FinishingMask;
        var rows = RasterRows.RowIndices(map.Height, RasterRows.RowStepCells(p.FinishingStepover, map.CellSize)).ToList();
        var passes = new List<Toolpath>();
        var forward = true;
        Toolpath? current = null;

        for (var r = 0; r < rows.Count; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            var j = rows[r];
            foreach (var (i0, i1) in RasterRows.Runs(i => mask[i, j] && !float.IsNaN(map[i, j]), map.Width, forward))
            {
                var points = RowPoints(map, i0, i1, j);
                var start = points[0];
                var join = current is null ? default : new ToolpathSegment(current.Segments[^1].End, start, MoveKind.Feed, p.FeedRate);
                if (current is not null && p.Direction == MillingDirection.Zigzag && GougeChecker.IsClear(join, map, p.Tolerance))
                {
                    current.Add(join);
                }
                else
                {
                    current = new Toolpath();
                    passes.Add(current);
                }

                if (points.Count == 1)
                {
                    current.Add(new ToolpathSegment(start, start, MoveKind.Feed, p.FeedRate));
                }

                for (var k = 1; k < points.Count; k++)
                {
                    current.Add(new ToolpathSegment(points[k - 1], points[k], MoveKind.Feed, p.FeedRate));
                }
            }

            if (p.Direction == MillingDirection.Zigzag)
            {
                forward = !forward;
            }

            progress?.Report((r + 1f) / rows.Count);
        }

        return ToolpathLinker.Link(passes, p, context.SafeZ, map);
    }

    // First cell center at its tip, every cell boundary at the higher of the two tips it separates,
    // last cell center at its tip; straight stretches are merged into one segment. Travels from first
    // to last. The interior points lift the tool by half a cell of rise on a slope instead of the
    // sawtooth a center-and-boundary staircase leaves, and a constant slope becomes one line.
    public static List<Vector3> RowPoints(HeightMap map, int first, int last, int j)
    {
        var y = map.CellCenter(0, j).Y;
        var step = first <= last ? 1 : -1;
        var points = new List<Vector3>();
        var i = first;
        Append(points, new Vector3(map.CellCenter(i, j).X, y, map[i, j]));
        while (i != last)
        {
            var next = i + step;
            var boundaryX = (map.CellCenter(i, j).X + map.CellCenter(next, j).X) / 2;
            Append(points, new Vector3(boundaryX, y, MathF.Max(map[i, j], map[next, j])));
            i = next;
        }

        if (last != first)
        {
            Append(points, new Vector3(map.CellCenter(last, j).X, y, map[last, j]));
        }

        return points;
    }

    private static void Append(List<Vector3> points, Vector3 point)
    {
        if (points.Count >= 1 && points[^1] == point)
        {
            return;
        }

        if (points.Count >= 2 && Collinear(points[^2], points[^1], point))
        {
            points[^1] = point;
            return;
        }

        points.Add(point);
    }

    private static bool Collinear(Vector3 a, Vector3 b, Vector3 c)
    {
        var u = b - a;
        var v = c - b;
        var lu = u.Length();
        var lv = v.Length();
        if (lu == 0 || lv == 0)
        {
            return true;
        }

        return Vector3.Dot(u, v) > 0 && Vector3.Cross(u, v).Length() <= MergeTolerance * lu * lv;
    }
}
