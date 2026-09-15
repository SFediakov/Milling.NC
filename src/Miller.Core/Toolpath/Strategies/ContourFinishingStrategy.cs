using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths.Strategies;

// Constant-Z loops. Levels run from the stock top down by FinishingStepover to the lowest effective
// tip (last level clamped). At each level the cells where the cutter may sit (tip <= level) form a
// mask whose outline, pulled into the allowed cells, becomes one feed loop per contour.
public sealed class ContourFinishingStrategy : IToolpathStrategy
{
    public const string StrategyId = "contour-finishing";

    public string Id => StrategyId;

    public string DisplayName => "Contour finishing (Z levels)";

    public MillingOperation Operation => MillingOperation.Finishing;

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var levels = Slicer.RoughingLevels(context.StockTop, context.Plan.LowestLevel, p.FinishingStepover).ToList();
        var groups = new List<IReadOnlyList<Toolpath>>();
        for (var k = 0; k < levels.Count; k++)
        {
            cancellation.ThrowIfCancellationRequested();
            var level = levels[k];
            groups.Add(LoopPasses(AllowedMask(map, level), map, level, p));
            progress?.Report((k + 1f) / levels.Count);
        }

        return ToolpathLinker.Link(groups, p, context.SafeZ, map);
    }

    // One closed feed loop at the level per outline of the mask, pulled into the allowed cells.
    public static List<Toolpath> LoopPasses(bool[,] mask, HeightMap map, float level, CuttingParameters p)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(p);
        var passes = new List<Toolpath>();
        foreach (var loop in MarchingSquares.MaskContours(mask, map))
        {
            var pass = new Toolpath();
            for (var n = 1; n < loop.Count; n++)
            {
                pass.Add(new ToolpathSegment(
                    new Vector3(loop[n - 1].X, loop[n - 1].Y, level),
                    new Vector3(loop[n].X, loop[n].Y, level),
                    MoveKind.Feed,
                    p.FeedRate));
            }

            passes.Add(pass);
        }

        return passes;
    }

    // Cells where the cutter tip may sit at the level.
    public static bool[,] AllowedMask(HeightMap tip, float level)
    {
        var mask = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                var z = tip[i, j];
                mask[i, j] = !float.IsNaN(z) && z <= level;
            }
        }

        return mask;
    }
}
