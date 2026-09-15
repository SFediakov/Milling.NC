using Miller.Core.Geometry;

namespace Miller.Core.HeightMaps;

// Top-down rasterization: a cell takes the highest Z of every triangle covering its center. Faces
// hidden below other faces disappear, which is exactly what a 3-axis mill can reach from +Z.
public static class MeshRasterizer
{
    // Barycentric weights this far below zero still count as inside, so a cell center lying exactly
    // on the shared edge of two triangles is covered by both instead of by neither.
    public const float BarycentricTolerance = 1e-5f;

    // Twice the projected XY area (mm^2) below which a triangle is a vertical wall and has no top.
    public const float MinProjectedArea = 1e-7f;

    // Guards ceil() against 20 / 0.5 evaluating to 40.0000004 and producing an extra cell.
    private const float CellCountEpsilon = 1e-4f;

    public static HeightMap CreateGridFor(BoundingBox bounds, float cellSize, float fill)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("Cannot create a grid for empty bounds.", nameof(bounds));
        }

        var width = Math.Max(1, (int)MathF.Ceiling(bounds.Size.X / cellSize - CellCountEpsilon));
        var height = Math.Max(1, (int)MathF.Ceiling(bounds.Size.Y / cellSize - CellCountEpsilon));
        return new HeightMap(bounds.Min.X, bounds.Min.Y, cellSize, width, height, fill);
    }

    public static void Rasterize(Mesh mesh, HeightMap target, float floor)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(target);
        target.Fill(floor);
        foreach (var triangle in mesh.Triangles)
        {
            RasterizeTriangle(triangle, target);
        }
    }

    // Only faces whose normal points down (Normal.Z < 0); uncovered cells stay NaN.
    public static void RasterizeDownwardFacing(Mesh mesh, HeightMap target)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(target);
        target.Fill(float.NaN);
        foreach (var triangle in mesh.Triangles)
        {
            if (triangle.Normal.Z < 0)
            {
                RasterizeTriangle(triangle, target);
            }
        }
    }

    private static void RasterizeTriangle(in Triangle t, HeightMap target)
    {
        float ax = t.A.X, ay = t.A.Y, bx = t.B.X, by = t.B.Y, cx = t.C.X, cy = t.C.Y;
        var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (MathF.Abs(area) < MinProjectedArea)
        {
            return;
        }

        var invArea = 1f / area;
        var (i0, j0) = target.CellOf(MathF.Min(ax, MathF.Min(bx, cx)), MathF.Min(ay, MathF.Min(by, cy)));
        var (i1, j1) = target.CellOf(MathF.Max(ax, MathF.Max(bx, cx)), MathF.Max(ay, MathF.Max(by, cy)));
        i0 = Math.Max(i0, 0);
        j0 = Math.Max(j0, 0);
        i1 = Math.Min(i1, target.Width - 1);
        j1 = Math.Min(j1, target.Height - 1);

        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var p = target.CellCenter(i, j);
                var wa = ((bx - p.X) * (cy - p.Y) - (cx - p.X) * (by - p.Y)) * invArea;
                var wb = ((cx - p.X) * (ay - p.Y) - (ax - p.X) * (cy - p.Y)) * invArea;
                var wc = 1f - wa - wb;
                if (wa < -BarycentricTolerance || wb < -BarycentricTolerance || wc < -BarycentricTolerance)
                {
                    continue;
                }

                var z = wa * t.A.Z + wb * t.B.Z + wc * t.C.Z;
                var current = target[i, j];
                if (float.IsNaN(current) || z > current)
                {
                    target[i, j] = z;
                }
            }
        }
    }
}
