using System.Numerics;
using Miller.Core.HeightMaps;

namespace Miller.Core.Simulation;

// Inclusive cell rectangle touched by a sweep; Empty has inverted bounds so Union works from it.
public readonly record struct DirtyRect(int I0, int J0, int I1, int J1)
{
    public static DirtyRect Empty => new(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);

    public bool IsEmpty => I1 < I0 || J1 < J0;

    public int Width => IsEmpty ? 0 : I1 - I0 + 1;

    public int Height => IsEmpty ? 0 : J1 - J0 + 1;

    public DirtyRect Union(DirtyRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return new DirtyRect(Math.Min(I0, other.I0), Math.Min(J0, other.J0), Math.Max(I1, other.I1), Math.Max(J1, other.J1));
    }

    public DirtyRect Include(int i, int j) => Union(new DirtyRect(i, j, i, j));
}

// Sweeps one segment through the stock: the tool tip is sampled at most CellSize / 2 apart along
// the segment, both end points included, and every footprint cell takes min(stock, z + dz).
// The tip is placed on the center of the cell it falls in, the same model as the gouge checker.
public static class MaterialRemover
{
    public static DirtyRect Sweep(HeightMap stock, ToolProfile profile, Vector3 from, Vector3 to)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.CellSize != stock.CellSize)
        {
            throw new ArgumentException($"Profile cell size {profile.CellSize} differs from the stock cell size {stock.CellSize}.", nameof(profile));
        }

        var spacing = stock.CellSize / 2;
        var length = Vector3.Distance(from, to);
        var steps = length > 0 ? (int)MathF.Ceiling(length / spacing) : 0;
        var dirty = DirtyRect.Empty;
        for (var s = 0; s <= steps; s++)
        {
            var t = steps == 0 ? 0f : (float)s / steps;
            dirty = dirty.Union(Stamp(stock, profile, Vector3.Lerp(from, to, t)));
        }

        return dirty;
    }

    // One tool position; returns the cells that got lower.
    public static DirtyRect Stamp(HeightMap stock, ToolProfile profile, Vector3 tip)
    {
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        var dirty = DirtyRect.Empty;
        foreach (var offset in profile.Offsets)
        {
            var i = ci + offset.Dx;
            var j = cj + offset.Dy;
            if (!stock.InBounds(i, j))
            {
                continue;
            }

            var current = stock[i, j];
            var cut = tip.Z + offset.Dz;
            if (float.IsNaN(current) || !(cut < current))
            {
                continue;
            }

            stock[i, j] = cut;
            dirty = dirty.Include(i, j);
        }

        return dirty;
    }
}
