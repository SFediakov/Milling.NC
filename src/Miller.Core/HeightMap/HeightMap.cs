// PLACEHOLDER - implemented by T-022 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.HeightMap
// Purpose: Uniform XY grid of Z values. float.NaN means no material. The cell sample point is the
//     cell center. Shared by model map, tip map, head limit, stock and deviation.
// Public interface (names only): sealed class HeightMap { float OriginX; float OriginY; float
//     CellSize; int Width; int Height; float[] Z; float this[int i, int j]; HeightMap(float
//     originX, float originY, float cellSize, int width, int height, float fill); Vector2
//     CellCenter(int i, int j); (int i, int j) CellOf(float x, float y); bool InBounds(int i, int
//     j); HeightMap Clone(); void Fill(float); float Min(); float Max(); BoundingBox Bounds(float
//     zMin, float zMax) }
// Depends on: BoundingBox
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
