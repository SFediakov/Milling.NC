using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths.Strategies;

// Z-level clearing. For every roughing level: rows along X spaced by Stepover (the last grid row is
// always included); runs of masked cells in a row become one feed segment at the level. Zigzag
// alternates the row direction and joins consecutive rows with a feed when the straight join stays
// on masked cells; otherwise, and always for OneWay, the linker retracts and plunges.
public sealed class RasterRoughingStrategy : IToolpathStrategy
{
    public const string StrategyId = "raster-roughing";

    // Guards floor() against 3 / 0.5 evaluating to 5.9999995.
    private const float RowStepEpsilon = 1e-4f;

    public string Id => StrategyId;

    public string DisplayName => "Raster roughing (Z levels)";

    public MillingOperation Operation => MillingOperation.Roughing;

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var rowStep = RowStepCells(p.Stepover, map.CellSize);
        var steps = context.Plan.RoughingSteps.ToList();
        var passes = new List<Toolpath>();

        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            var forward = true;
            Toolpath? current = null;
            foreach (var j in RowIndices(map.Height, rowStep))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var (i0, i1) in Runs(step.Mask, j, map.Width, forward))
                {
                    var start = Point(map, i0, j, step.Level);
                    var end = Point(map, i1, j, step.Level);
                    if (current is not null && p.Direction == MillingDirection.Zigzag
                        && StaysOnMask(step.Mask, map, current.Segments[^1].End, start))
                    {
                        current.Add(new ToolpathSegment(current.Segments[^1].End, start, MoveKind.Feed, p.FeedRate));
                    }
                    else
                    {
                        current = new Toolpath();
                        passes.Add(current);
                    }

                    current.Add(new ToolpathSegment(start, end, MoveKind.Feed, p.FeedRate));
                }

                if (p.Direction == MillingDirection.Zigzag)
                {
                    forward = !forward;
                }
            }

            progress?.Report((s + 1f) / steps.Count);
        }

        return ToolpathLinker.Link(passes, p, context.SafeZ);
    }

    public static int RowStepCells(float stepover, float cellSize)
        => Math.Max(1, (int)MathF.Floor(stepover / cellSize + RowStepEpsilon));

    public static IEnumerable<int> RowIndices(int height, int rowStep)
    {
        var last = -1;
        for (var j = 0; j < height; j += rowStep)
        {
            last = j;
            yield return j;
        }

        if (last != height - 1)
        {
            yield return height - 1;
        }
    }

    // Runs of consecutive masked cells in row j as (first, last) in travel order.
    public static IEnumerable<(int First, int Last)> Runs(bool[,] mask, int j, int width, bool forward)
    {
        var runs = new List<(int, int)>();
        var i = 0;
        while (i < width)
        {
            if (!mask[i, j])
            {
                i++;
                continue;
            }

            var start = i;
            while (i + 1 < width && mask[i + 1, j])
            {
                i++;
            }

            runs.Add((start, i));
            i++;
        }

        if (forward)
        {
            return runs;
        }

        runs.Reverse();
        return runs.Select(r => (r.Item2, r.Item1));
    }

    // Every half-cell sample of the straight join lies on a masked cell.
    public static bool StaysOnMask(bool[,] mask, HeightMap map, Vector3 from, Vector3 to)
    {
        var length = Vector3.Distance(from, to);
        var samples = length > 0 ? (int)MathF.Ceiling(length / (map.CellSize / 2)) : 0;
        for (var k = 0; k <= samples; k++)
        {
            var q = samples == 0 ? from : Vector3.Lerp(from, to, (float)k / samples);
            var (i, j) = map.CellOf(q.X, q.Y);
            if (!map.InBounds(i, j) || !mask[i, j])
            {
                return false;
            }
        }

        return true;
    }

    private static Vector3 Point(HeightMap map, int i, int j, float z)
    {
        var c = map.CellCenter(i, j);
        return new Vector3(c.X, c.Y, z);
    }
}
