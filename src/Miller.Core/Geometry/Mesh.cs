using System.Numerics;

namespace Miller.Core.Geometry;

public sealed class Mesh
{
    private readonly Triangle[] _triangles;

    public Mesh(IEnumerable<Triangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        _triangles = triangles.ToArray();
        var bounds = BoundingBox.Empty;
        foreach (var triangle in _triangles)
        {
            bounds = bounds.Include(triangle.A).Include(triangle.B).Include(triangle.C);
        }

        Bounds = bounds;
    }

    public IReadOnlyList<Triangle> Triangles => _triangles;

    public int TriangleCount => _triangles.Length;

    public BoundingBox Bounds { get; }

    // Every vertex through the matrix as Vector3.Transform does it (native mn_transform_points). A
    // mirroring transform (negative determinant) reverses the winding, so B and C are swapped to keep
    // the recomputed normals pointing outward.
    public unsafe Mesh Transform(Matrix4x4 matrix)
    {
        var mirrored = matrix.GetDeterminant() < 0;
        var points = new float[Math.Max(_triangles.Length * 9, 1)];
        for (var i = 0; i < _triangles.Length; i++)
        {
            var t = _triangles[i];
            points[9 * i] = t.A.X;
            points[9 * i + 1] = t.A.Y;
            points[9 * i + 2] = t.A.Z;
            points[9 * i + 3] = t.B.X;
            points[9 * i + 4] = t.B.Y;
            points[9 * i + 5] = t.B.Z;
            points[9 * i + 6] = t.C.X;
            points[9 * i + 7] = t.C.Y;
            points[9 * i + 8] = t.C.Z;
        }

        var result = new float[points.Length];
        var rows = new[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        };
        fixed (float* source = points, target = result, m = rows)
        {
            Native.CoreNative.mn_transform_points(source, _triangles.Length * 3, m, target);
        }

        var transformed = new Triangle[_triangles.Length];
        for (var i = 0; i < _triangles.Length; i++)
        {
            var a = new Vector3(result[9 * i], result[9 * i + 1], result[9 * i + 2]);
            var b = new Vector3(result[9 * i + 3], result[9 * i + 4], result[9 * i + 5]);
            var c = new Vector3(result[9 * i + 6], result[9 * i + 7], result[9 * i + 8]);
            transformed[i] = mirrored ? new Triangle(a, c, b) : new Triangle(a, b, c);
        }

        return new Mesh(transformed);
    }

    public Mesh RemoveDegenerate()
        => new(_triangles.Where(t => !t.IsDegenerate));
}
