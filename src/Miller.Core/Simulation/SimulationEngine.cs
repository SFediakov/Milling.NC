// PLACEHOLDER - implemented by T-089 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Simulation
// Purpose: Walks the toolpath by distance = rate(kind) * simSeconds / 60, sweeping feed and plunge
//     parts through the stock; RunToEnd for the final model.
// Public interface (names only): sealed record StepResult(Vector3 ToolPosition, DirtyRect Dirty,
//     int SegmentsCompleted, bool Finished); sealed class SimulationEngine {
//     SimulationEngine(Toolpath toolpath, HeightMap stock, ToolProfile profile, CuttingParameters
//     parameters); HeightMap Stock; Vector3 ToolPosition; int CurrentSegmentIndex; float Progress;
//     bool IsFinished; StepResult Step(double simSeconds); void RunToEnd(); void Reset(HeightMap
//     freshStock) }
// Depends on: Toolpath, HeightMap, ToolProfile, MaterialRemover, CuttingParameters
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
