// PLACEHOLDER - implemented by T-091 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Services
// Purpose: Owns engine, clock and collision detector for the loaded pipeline result;
//     Advance(realSeconds) returns a snapshot for the viewport.
// Public interface (names only): sealed record SimulationSnapshot(Vector3 ToolPosition, DirtyRect
//     Dirty, IReadOnlyList<SimulationEvent> NewEvents, float Progress, double ElapsedSimulated,
//     bool Finished); sealed class SimulationService { bool IsLoaded; bool IsPlaying; float
//     SpeedFactor; HeightMap? Stock; IReadOnlyList<SimulationEvent> Events; void
//     Load(PipelineResult result); void Play(); void Pause(); void Stop(); SimulationSnapshot
//     StepOnce(double simSeconds); void RunToEnd(); SimulationSnapshot Advance(double realSeconds)
//     }
// Depends on: SimulationEngine, SimulationClock, CollisionDetector, StockModel
// Must not depend on: Avalonia and any UI type; Miller.App
