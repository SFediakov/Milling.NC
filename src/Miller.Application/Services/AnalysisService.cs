// PLACEHOLDER - implemented by T-097 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Services
// Purpose: Runs the engine to the end on a fresh stock and analyzes the result on a background
//     task.
// Public interface (names only): sealed class AnalysisService { Task<AnalysisResult>
//     AnalyzeAsync(PipelineResult result, CancellationToken cancellation); Task<UncuttableResult>
//     UncuttableAsync(PipelineResult result, CancellationToken cancellation) }
// Depends on: SimulationEngine, FinalModelAnalyzer, UncuttableRegions
// Must not depend on: Avalonia and any UI type; Miller.App
