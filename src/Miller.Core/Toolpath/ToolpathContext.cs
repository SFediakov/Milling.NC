using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths;

// Everything a strategy needs, prepared once by the pipeline. StockTop is the Z of the untouched
// stock surface; SafeZ is the absolute rapid height (stock top + SafeHeight clearance).
public sealed record ToolpathContext(
    HeightMap Model,
    HeightMap Tip,
    HeightMap EffectiveTip,
    HeightMap HeadLimit,
    HeightMap Stock,
    SlicePlan Plan,
    ToolDefinition Tool,
    ToolProfile Profile,
    CuttingParameters Parameters,
    float StockTop)
{
    public float SafeZ => StockTop + Parameters.SafeHeight;

    // Cells the head collided with that do not belong to the model (the collision check of the
    // pipeline): they must be cut down to their floor before the tool works deeper beside them.
    public bool[,]? ShouldCut { get; init; }
}
