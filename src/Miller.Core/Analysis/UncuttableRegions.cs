// PLACEHOLDER - implemented by T-098 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Analysis
// Purpose: Masks of what a 3-axis mill cannot produce: Overhang (downward-facing surface below the
//     top surface), HeadLimited (from HeadClearance), CornerLimited (effective tip above model by
//     more than tolerance, not head-limited).
// Public interface (names only): sealed record UncuttableResult(bool[,] Overhang, bool[,]
//     HeadLimited, bool[,] CornerLimited, int OverhangCells, int HeadLimitedCells, int
//     CornerLimitedCells); static class UncuttableRegions { static UncuttableResult Compute(Mesh
//     machineMesh, HeightMap model, HeightMap tip, HeightMap effectiveTip, bool[,] headLimited,
//     float floor, float tolerance) }
// Depends on: Mesh, HeightMap, MeshRasterizer
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
