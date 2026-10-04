using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Native;

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

// Sweeps one segment through the stock (native mn_sweep): the tool tip is sampled at most CellSize / 2
// apart along the segment, both end points included, and every footprint cell takes min(stock, z + dz).
// The tip is placed on the center of the cell it falls in, the same model as the gouge checker. The
// native sweep stamps consecutive samples of one cell once with their lowest z and walks the footprint
// as row spans, which gives the same cells bit for bit.
public static class MaterialRemover
{
    public static unsafe DirtyRect Sweep(HeightMap stock, ToolProfile profile, Vector3 from, Vector3 to)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.CellSize != stock.CellSize)
        {
            throw new ArgumentException($"Profile cell size {profile.CellSize} differs from the stock cell size {stock.CellSize}.", nameof(profile));
        }

        var grid = CoreNative.GridOf(stock);
        var offsets = profile.NativeOffsets;
        var start = stackalloc float[] { from.X, from.Y, from.Z };
        var end = stackalloc float[] { to.X, to.Y, to.Z };
        CoreNative.Dirty dirty;
        fixed (float* z = stock.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.Check(CoreNative.mn_sweep(z, &grid, o, offsets.Length, start, end, &dirty));
        }

        return new DirtyRect(dirty.I0, dirty.J0, dirty.I1, dirty.J1);
    }

    // One tool position; returns the cells that got lower.
    public static unsafe DirtyRect Stamp(HeightMap stock, ToolProfile profile, Vector3 tip)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        var grid = CoreNative.GridOf(stock);
        var offsets = profile.NativeOffsets;
        var point = stackalloc float[] { tip.X, tip.Y, tip.Z };
        CoreNative.Dirty dirty;
        fixed (float* z = stock.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.Check(CoreNative.mn_stamp(z, &grid, o, offsets.Length, point, &dirty));
        }

        return new DirtyRect(dirty.I0, dirty.J0, dirty.I1, dirty.J1);
    }
}
