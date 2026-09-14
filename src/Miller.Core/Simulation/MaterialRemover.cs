// PLACEHOLDER - implemented by T-088 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Simulation
// Purpose: Sweeps one segment through the stock: samples at most CellSize / 2 apart, stock =
//     min(stock, z + dz) over the footprint, NaN skipped. Returns the dirty cell rectangle.
// Public interface (names only): readonly record struct DirtyRect(int I0, int J0, int I1, int J1) {
//     static DirtyRect Empty; DirtyRect Union(DirtyRect); bool IsEmpty }; static class
//     MaterialRemover { static DirtyRect Sweep(HeightMap stock, ToolProfile profile, Vector3 from,
//     Vector3 to) }
// Depends on: HeightMap, ToolProfile
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
