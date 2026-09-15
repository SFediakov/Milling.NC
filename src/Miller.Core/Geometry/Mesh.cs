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

    public Mesh Transform(Matrix4x4 matrix)
    {
        var transformed = new Triangle[_triangles.Length];
        for (var i = 0; i < _triangles.Length; i++)
        {
            var t = _triangles[i];
            transformed[i] = new Triangle(
                Vector3.Transform(t.A, matrix),
                Vector3.Transform(t.B, matrix),
                Vector3.Transform(t.C, matrix));
        }

        return new Mesh(transformed);
    }

    public Mesh RemoveDegenerate()
        => new(_triangles.Where(t => !t.IsDegenerate));
}
