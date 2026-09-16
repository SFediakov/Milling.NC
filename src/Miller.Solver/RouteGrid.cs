namespace Miller.Solver;

// The surface the tool must stay on or above while it travels, as a flat row-major array:
// Floor[j * Width + i] is the height of cell (i, j). NaN means nothing stands in that cell, so it
// does not constrain the tool; cells outside the grid count as NaN as well.
public sealed class RouteGrid
{
    public RouteGrid(float[] floor, int width, int height, float originX, float originY, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Grid size must be positive, got {width} x {height}.");
        }

        if (floor.Length != width * height)
        {
            throw new ArgumentException($"{floor.Length} cells for a {width} x {height} grid.", nameof(floor));
        }

        if (!(cellSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        Floor = floor;
        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
    }

    public float[] Floor { get; }

    public int Width { get; }

    public int Height { get; }

    public float OriginX { get; }

    public float OriginY { get; }

    public float CellSize { get; }

    public bool InBounds(int i, int j) => i >= 0 && i < Width && j >= 0 && j < Height;

    public float At(int i, int j) => InBounds(i, j) ? Floor[j * Width + i] : float.NaN;

    public int CellI(float x) => (int)MathF.Floor((x - OriginX) / CellSize);

    public int CellJ(float y) => (int)MathF.Floor((y - OriginY) / CellSize);
}
