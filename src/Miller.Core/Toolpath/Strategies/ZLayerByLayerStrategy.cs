using Miller.Core.Progress;

namespace Miller.Core.Toolpaths.Strategies;

// Cave by cave, level by level (native mn_z_layer). The level masks form a tree of caves (CaveTree); a
// cave is cut completely at its level along the fastest route through its nodes (NodeLattice at the
// Stepover, RouteSolver), then the tool drops one level into the first child cave, and it rises for
// the next sibling only when a whole subtree is done. Every route is solved over the material as it
// will stand at that moment: the cells of the cave at its level, everything else at what the previous
// routes left, so a move that leaves the cave climbs the standing material instead of cutting a slot
// through it. Between caves the writer chooses the surface polyline or a retract by time.
public sealed class ZLayerByLayerStrategy : IToolpathStrategy
{
    public const string StrategyId = "z-layer-by-layer";

    public string Id => StrategyId;

    public string DisplayName => "Z layer by layer";

    public Toolpath Generate(ToolpathContext context, IProgress<StepProgress>? progress, CancellationToken cancellation)
        => NativeStrategy.Generate(NativeStrategy.ZLayer, context, progress, cancellation);
}
