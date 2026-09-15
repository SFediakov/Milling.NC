using System.Numerics;

namespace Miller.Core.Geometry;

public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    public static BoundingBox Empty { get; } = new(
        new Vector3(float.PositiveInfinity),
        new Vector3(float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) * 0.5f;

    public BoundingBox Union(BoundingBox other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return new BoundingBox(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
    }

    public BoundingBox Include(Vector3 point)
        => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    public bool Contains(Vector3 point)
        => point.X >= Min.X && point.X <= Max.X
        && point.Y >= Min.Y && point.Y <= Max.Y
        && point.Z >= Min.Z && point.Z <= Max.Z;

    public bool Contains(BoundingBox other)
        => other.IsEmpty || (Contains(other.Min) && Contains(other.Max));

    // Slab test: distance along the ray to the first hit, or null when the ray misses. A ray that
    // starts inside the box hits at distance 0; only forward hits count.
    public float? IntersectRay(Vector3 origin, Vector3 direction)
    {
        if (IsEmpty)
        {
            return null;
        }

        var near = 0f;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = Component(origin, axis);
            var d = Component(direction, axis);
            var lo = Component(Min, axis);
            var hi = Component(Max, axis);
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi)
                {
                    return null;
                }

                continue;
            }

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
            {
                return null;
            }
        }

        return near;
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
