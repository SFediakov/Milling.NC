using Miller.Core.Geometry;
using Miller.Core.Native;

namespace Miller.Core.HeightMaps;

// Top-down rasterization (native mn_rasterize): a cell takes the highest Z of every triangle covering
// its center, with barycentric weights 1e-5 below zero still inside and triangles of less than 1e-7
// mm^2 projected area skipped. Faces hidden below other faces disappear, which is exactly what a
// 3-axis mill can reach from +Z.
public static class MeshRasterizer
{
    public const float BarycentricTolerance = 1e-5f;

    public const float MinProjectedArea = 1e-7f;

    public static unsafe HeightMap CreateGridFor(BoundingBox bounds, float cellSize, float fill)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("Cannot create a grid for empty bounds.", nameof(bounds));
        }

        CoreNative.Grid grid;
        CoreNative.Check(CoreNative.mn_grid_for(bounds.Min.X, bounds.Min.Y, bounds.Max.X, bounds.Max.Y, cellSize, &grid));
        return new HeightMap(grid.OriginX, grid.OriginY, grid.CellSize, grid.Width, grid.Height, fill);
    }

    public static void Rasterize(Mesh mesh, HeightMap target, float floor)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(target);
        target.Fill(floor);
        Draw(mesh.Triangles, target);
    }

    // Only faces whose normal points down (Normal.Z < 0); uncovered cells stay NaN.
    public static void RasterizeDownwardFacing(Mesh mesh, HeightMap target)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(target);
        target.Fill(float.NaN);
        Draw(mesh.Triangles.Where(t => t.Normal.Z < 0).ToList(), target);
    }

    private static unsafe void Draw(IReadOnlyList<Triangle> triangles, HeightMap target)
    {
        var flat = new float[Math.Max(triangles.Count * 9, 1)];
        for (var k = 0; k < triangles.Count; k++)
        {
            var t = triangles[k];
            flat[9 * k] = t.A.X;
            flat[9 * k + 1] = t.A.Y;
            flat[9 * k + 2] = t.A.Z;
            flat[9 * k + 3] = t.B.X;
            flat[9 * k + 4] = t.B.Y;
            flat[9 * k + 5] = t.B.Z;
            flat[9 * k + 6] = t.C.X;
            flat[9 * k + 7] = t.C.Y;
            flat[9 * k + 8] = t.C.Z;
        }

        var grid = CoreNative.GridOf(target);
        fixed (float* data = flat, z = target.Z)
        {
            CoreNative.mn_rasterize(data, triangles.Count, &grid, z);
        }
    }
}
