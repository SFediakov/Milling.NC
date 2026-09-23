using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Progress;

namespace Miller.Core.Toolpaths.Strategies;

// Free 3-axis moves over the surface, one stepdown at a time (native mn_three_axis_freedom). The levels
// are the plan's (stock top minus k x Stepdown, the last one at the lowest tip). At level L the nodes
// are the coverage cells whose effective tip lies below the previous level (they still carry
// material), on the FinishingStepover lattice plus every cell where the level map max(tip, L) steps by
// more than the tolerance to a neighbour (walls and edges), each at max(tip, L). One route per level
// is the fastest the solver finds and every move follows the surface polyline over the level map, so
// the tool is never under the effective tip and never deeper than one Stepdown into the material the
// previous level left. The last visit of a cell is at its tip, so the surface is followed without
// level quantization where it lies between two levels.
public sealed class ThreeAxisFreedomStrategy : IToolpathStrategy
{
    public const string StrategyId = "three-axis-freedom";

    // Id of the same strategy before the one-stepdown rule; project files still naming it load.
    public const string LegacyStrategyId = "three-axis-precise";

    public string Id => StrategyId;

    public string DisplayName => "3 axis freedom";

    public Toolpath Generate(ToolpathContext context, IProgress<StepProgress>? progress, CancellationToken cancellation)
        => NativeStrategy.Generate(NativeStrategy.ThreeAxisFreedom, context, progress, cancellation);

    // The surface as it stands once the level is cut: the effective tip where it is above the level,
    // the level elsewhere; NaN stays NaN.
    public static unsafe HeightMap LevelMap(HeightMap tip, float level)
    {
        ArgumentNullException.ThrowIfNull(tip);
        var result = CoreNative.Empty(tip, float.NaN);
        fixed (float* source = tip.Z, target = result.Z)
        {
            CoreNative.mn_level_map(source, tip.CellCount, level, target);
        }

        return result;
    }

    // True where a neighbour's height differs by more than the tolerance or holds no material.
    public static unsafe bool IsStep(HeightMap map, int i, int j, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(map);
        var grid = CoreNative.GridOf(map);
        fixed (float* z = map.Z)
        {
            return CoreNative.mn_is_step(&grid, z, i, j, tolerance) != 0;
        }
    }
}
