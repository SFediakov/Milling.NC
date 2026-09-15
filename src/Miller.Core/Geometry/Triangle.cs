using System.Numerics;

namespace Miller.Core.Geometry;

public readonly struct Triangle
{
    // Square millimetres. Single precision at part sizes of hundreds of millimetres carries an
    // absolute error around 1e-5 mm per coordinate, so cross-product areas below this value are
    // numerical noise rather than geometry.
    public const float MinArea = 1e-6f;

    public Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        A = a;
        B = b;
        C = c;
        var cross = Vector3.Cross(b - a, c - a);
        var doubleArea = cross.Length();
        IsDegenerate = !(doubleArea * 0.5f >= MinArea);
        Normal = IsDegenerate ? Vector3.Zero : cross / doubleArea;
    }

    public Vector3 A { get; }

    public Vector3 B { get; }

    public Vector3 C { get; }

    // Unit normal by the right-hand rule over A, B, C; the STL file normal is not trusted.
    public Vector3 Normal { get; }

    public bool IsDegenerate { get; }

    public float MinZ => MathF.Min(A.Z, MathF.Min(B.Z, C.Z));

    public float MaxZ => MathF.Max(A.Z, MathF.Max(B.Z, C.Z));

    public BoundingBox Bounds => BoundingBox.Empty.Include(A).Include(B).Include(C);
}
