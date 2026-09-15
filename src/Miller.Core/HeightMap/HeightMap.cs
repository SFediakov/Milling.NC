using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.HeightMaps;

// Uniform XY grid of Z values. Index i runs along X, j along Y; the sample point of cell (i, j) is
// its center. float.NaN means no material. The model map, tip map, head limit, stock and deviation
// map are all instances of this type so every algorithm shares one cell arithmetic.
public sealed class HeightMap
{
    public HeightMap(float originX, float originY, float cellSize, int width, int height, float fill)
    {
        if (!(cellSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Grid size must be positive, got {width} x {height}.");
        }

        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
        Width = width;
        Height = height;
        Z = new float[checked(width * height)];
        Fill(fill);
    }

    private HeightMap(HeightMap source)
    {
        OriginX = source.OriginX;
        OriginY = source.OriginY;
        CellSize = source.CellSize;
        Width = source.Width;
        Height = source.Height;
        Z = (float[])source.Z.Clone();
    }

    public float OriginX { get; }

    public float OriginY { get; }

    public float CellSize { get; }

    public int Width { get; }

    public int Height { get; }

    public float MaxX => OriginX + Width * CellSize;

    public float MaxY => OriginY + Height * CellSize;

    public int CellCount => Z.Length;

    // Row-major: index = j * Width + i.
    public float[] Z { get; }

    public float this[int i, int j]
    {
        get => Z[Index(i, j)];
        set => Z[Index(i, j)] = value;
    }

    public int Index(int i, int j)
    {
        if (!InBounds(i, j))
        {
            throw new IndexOutOfRangeException($"Cell ({i}, {j}) is outside the {Width} x {Height} grid.");
        }

        return j * Width + i;
    }

    public bool InBounds(int i, int j) => i >= 0 && i < Width && j >= 0 && j < Height;

    public Vector2 CellCenter(int i, int j)
        => new(OriginX + (i + 0.5f) * CellSize, OriginY + (j + 0.5f) * CellSize);

    // Cell containing the world point; may be outside the grid (check InBounds).
    public (int I, int J) CellOf(float x, float y)
        => ((int)MathF.Floor((x - OriginX) / CellSize), (int)MathF.Floor((y - OriginY) / CellSize));

    public HeightMap Clone() => new(this);

    public void Fill(float value) => Array.Fill(Z, value);

    // NaN when every cell is NaN.
    public float Min()
    {
        var min = float.NaN;
        foreach (var z in Z)
        {
            if (!float.IsNaN(z) && (float.IsNaN(min) || z < min))
            {
                min = z;
            }
        }

        return min;
    }

    public float Max()
    {
        var max = float.NaN;
        foreach (var z in Z)
        {
            if (!float.IsNaN(z) && (float.IsNaN(max) || z > max))
            {
                max = z;
            }
        }

        return max;
    }

    public int MaterialCellCount()
    {
        var count = 0;
        foreach (var z in Z)
        {
            if (!float.IsNaN(z))
            {
                count++;
            }
        }

        return count;
    }

    public bool SameGridAs(HeightMap other)
        => other.Width == Width && other.Height == Height && other.CellSize == CellSize
        && other.OriginX == OriginX && other.OriginY == OriginY;

    public BoundingBox Bounds(float zMin, float zMax)
        => new(new Vector3(OriginX, OriginY, zMin), new Vector3(MaxX, MaxY, zMax));
}
