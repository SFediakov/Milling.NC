// PLACEHOLDER - implemented by T-026 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.HeightMap
// Purpose: Tip map: the lowest tip height at each cell that does not gouge: tip[i,j] = max over
//     footprint of (model[i+dx,j+dy] - dz). NaN cells are skipped. Grid edges are clamped.
// Public interface (names only): static class HeightMapDilation { static HeightMap
//     ComputeTipMap(HeightMap model, ToolProfile profile) }
// Depends on: HeightMap, ToolProfile
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
