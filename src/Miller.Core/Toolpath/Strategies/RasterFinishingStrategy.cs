// PLACEHOLDER - implemented by T-041 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpath.Strategies
// Purpose: Id raster-finishing. Parallel rows at FinishingStepover following the effective tip map
//     cell by cell; collinear points merged; NaN rows skipped.
// Public interface (names only): sealed class RasterFinishingStrategy : IToolpathStrategy
// Depends on: IToolpathStrategy, ToolpathLinker, HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
