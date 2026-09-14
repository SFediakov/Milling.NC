// PLACEHOLDER - implemented by T-025 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.HeightMap
// Purpose: Tool footprint on the grid: offsets (dx, dy, dz) inside the cutter radius, dz = 0 for
//     flat and r - sqrt(r^2 - d^2) for ball; annulus offsets between cutter radius and head radius
//     for head checks.
// Public interface (names only): readonly record struct ProfileOffset(int Dx, int Dy, float Dz);
//     sealed class ToolProfile { ToolDefinition Tool; float CellSize; ProfileOffset[] Offsets;
//     ProfileOffset[] AnnulusOffsets; int RadiusCells; static ToolProfile Create(ToolDefinition
//     tool, float cellSize) }
// Depends on: ToolDefinition
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
