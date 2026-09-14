// PLACEHOLDER - implemented by T-039 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpath.Strategies
// Purpose: Id raster-roughing. Z-level clearing: per roughing level, parallel rows along X spaced
//     by Stepover; runs of masked cells become feed segments at the level; Zigzag alternates
//     direction; passes are linked.
// Public interface (names only): sealed class RasterRoughingStrategy : IToolpathStrategy
// Depends on: IToolpathStrategy, ToolpathLinker, SlicePlan
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
