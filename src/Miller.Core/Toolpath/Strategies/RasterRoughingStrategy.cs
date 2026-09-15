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

    public string Id => StrategyId;

    public string DisplayName => "Raster roughing (Z levels)";

    public MillingOperation Operation => MillingOperation.Roughing;

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var steps = context.Plan.RoughingSteps.ToList();
        var passes = new List<Toolpath>();

        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            passes.AddRange(RowPasses(map, step.Mask, step.Level, p, cancellation));
            progress?.Report((s + 1f) / steps.Count);
        }

        return ToolpathLinker.Link(passes, p, context.SafeZ, map);
    }

    // Rows along X spaced by Stepover over one level mask; runs of masked cells become feeds at the
    // level, joined in zigzag order while the straight join stays on the mask.
    public static List<Toolpath> RowPasses(HeightMap map, bool[,] mask, float level, CuttingParameters p, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(p);
        var rowStep = RasterRows.RowStepCells(p.Stepover, map.CellSize);
        var passes = new List<Toolpath>();
        var forward = true;
        Toolpath? current = null;
        foreach (var j in RasterRows.RowIndices(map.Height, rowStep))
        {
            cancellation.ThrowIfCancellationRequested();
            var row = j;
            foreach (var (i0, i1) in RasterRows.Runs(i => mask[i, row], map.Width, forward))
            {
                var start = Point(map, i0, j, level);
                var end = Point(map, i1, j, level);
                if (current is not null && p.Direction == MillingDirection.Zigzag
                    && StaysOnMask(mask, map, current.Segments[^1].End, start))
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

        return passes;
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
