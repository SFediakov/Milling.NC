// PLACEHOLDER - implemented by T-042 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpath
// Purpose: Iso-contours of a heightmap at a level as closed polylines with linear interpolation on
//     cell edges. NaN counts as below any level so loops close at stock borders.
// Public interface (names only): static class MarchingSquares { static
//     IReadOnlyList<IReadOnlyList<Vector2>> Contours(HeightMap map, float level) }
// Depends on: HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
