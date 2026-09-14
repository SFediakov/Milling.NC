// PLACEHOLDER - implemented by T-028 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.HeightMap
// Purpose: Head limit: limit[i,j] = max over annulus of model - CutterLength; effective tip =
//     max(tip, limit); head-limited mask where limit > tip + tolerance. Computed against the model
//     map, not the stock.
// Public interface (names only): static class HeadClearance { static HeightMap
//     ComputeHeadLimit(HeightMap model, ToolProfile profile, float cutterLength); static HeightMap
//     ApplyHeadLimit(HeightMap tip, HeightMap limit); static bool[,] HeadLimitedMask(HeightMap tip,
//     HeightMap limit, float tolerance) }
// Depends on: HeightMap, ToolProfile
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
