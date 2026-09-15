using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths.Strategies;

// Z-level clearing that finishes every layer before stepping down: at each roughing level the raster
// runs of the level mask are followed by a profile pass along the outline of that mask, so the strip
// the rows cannot reach beside walls is cut to the level as well. Only then the next level starts.
// This is the roughing strategy whose result matches the remaining-material model of the head limit.
public sealed class LayerCompleteStrategy : IToolpathStrategy
{
    public const string StrategyId = "layer-complete";

    public string Id => StrategyId;

    public string DisplayName => "Layer complete (clear and contour per level)";

    public MillingOperation Operation => MillingOperation.Roughing;

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var steps = context.Plan.RoughingSteps.ToList();
        var groups = new List<IReadOnlyList<Toolpath>>();
        for (var s = 0; s < steps.Count; s++)
        {
            cancellation.ThrowIfCancellationRequested();
            var step = steps[s];
            var level = new List<Toolpath>();
            level.AddRange(RasterRoughingStrategy.RowPasses(map, step.Mask, step.Level, p, cancellation));
            level.AddRange(ContourFinishingStrategy.LoopPasses(step.Mask, map, step.Level, p));
            groups.Add(level);
            progress?.Report((s + 1f) / steps.Count);
        }

        return ToolpathLinker.Link(groups, p, context.SafeZ, map);
    }
}
