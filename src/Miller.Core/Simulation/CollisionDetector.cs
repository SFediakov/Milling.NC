// PLACEHOLDER - implemented by T-090 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Simulation
// Purpose: Checks the current stock (not the model) at a tool position: head annulus above tip.Z +
//     CutterLength = HeadCollision; rapid sample below the stock under the footprint =
//     RapidIntoMaterial.
// Public interface (names only): static class CollisionDetector { static SimulationEvent?
//     Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind,
//     int segmentIndex) }
// Depends on: HeightMap, ToolProfile, SimulationEvent, MoveKind
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
