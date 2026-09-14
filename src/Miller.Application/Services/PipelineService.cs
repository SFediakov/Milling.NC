// PLACEHOLDER - implemented by T-047 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Services
// Purpose: Runs the whole toolpath pipeline of docs/ARCHITECTURE.md 5.1 on a background task with
//     validation, progress and cancellation.
// Public interface (names only): sealed record PipelineResult(Mesh MachineMesh, StockGeometry
//     Stock, HeightMap Model, HeightMap Tip, HeightMap EffectiveTip, HeightMap HeadLimit, bool[,]
//     HeadLimitedMask, SlicePlan Plan, Toolpath Toolpath, ToolpathStatistics Statistics,
//     ToolProfile Profile, float Floor); sealed class PipelineService { Task<PipelineResult>
//     RunAsync(MillingProject project, Mesh mesh, IProgress<ProgressReport> progress,
//     CancellationToken cancellation) }
// Depends on: ProjectValidator, AxisSetup, StockModel, MeshRasterizer, HeightMapDilation,
//     HeadClearance, Slicer, StrategyRegistry, ToolpathLinker, ToolpathStatistics
// Must not depend on: Avalonia and any UI type; Miller.App
