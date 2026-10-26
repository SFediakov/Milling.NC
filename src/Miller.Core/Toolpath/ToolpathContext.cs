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

    // The floor of the model map: the model stands where it lies above the floor (backward check).
    public float Floor { get; init; }

    // The backward check runs on the CPU or, with the generation's GPU session, on the GPU (T-169, T-174).
    public CollisionMode CollisionMode { get; init; } = CollisionMode.Backwards;
}
