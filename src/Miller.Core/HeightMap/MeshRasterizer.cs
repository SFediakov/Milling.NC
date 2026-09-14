// PLACEHOLDER - implemented by T-023 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.HeightMap
// Purpose: Top-down rasterization of a mesh: max Z per cell over all triangles covering the cell
//     center; uncovered cells keep the floor value. RasterizeDownwardFacing keeps only triangles
//     with Normal.Z < 0 (overhang detection).
// Public interface (names only): static class MeshRasterizer { static void Rasterize(Mesh mesh,
//     HeightMap target, float floor); static void RasterizeDownwardFacing(Mesh mesh, HeightMap
//     target); static HeightMap CreateGridFor(BoundingBox stockBounds, float cellSize, float fill)
//     }
// Depends on: Mesh, HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
