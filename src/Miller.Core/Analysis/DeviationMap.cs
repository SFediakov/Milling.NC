// PLACEHOLDER - implemented by T-096 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Analysis
// Purpose: Per-cell difference between the final stock and the model, with a category per cell.
// Public interface (names only): enum CellCategory { Ok, RestMaterial, Gouge, NoModel, Overhang,
//     HeadLimited, CornerLimited }; sealed class DeviationMap { HeightMap Values; CellCategory[]
//     Categories; int Count(CellCategory) }
// Depends on: HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
