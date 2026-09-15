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
}
