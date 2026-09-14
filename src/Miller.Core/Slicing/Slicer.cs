// PLACEHOLDER - implemented by T-034 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Slicing
// Purpose: Decomposes the job into steps: levels z_k = stockTop - k * Stepdown until the lowest
//     effective tip (last level clamped), roughing mask = cells with effectiveTip < level and stock
//     above level, one finishing step.
// Public interface (names only): static class Slicer { static SlicePlan Build(HeightMap
//     effectiveTip, HeightMap stock, CuttingParameters parameters) }
// Depends on: HeightMap, CuttingParameters, MillingStep, SlicePlan
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
