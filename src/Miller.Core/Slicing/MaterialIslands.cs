using Miller.Core.HeightMaps;
using Miller.Core.Native;

namespace Miller.Core.Slicing;

// One island: an 8-connected region of standing stock the trench encloses, its cells as flat
// indices (j * width + i) and the material milling it out would remove.
public sealed record MaterialIsland(int[] Cells, float Volume);

// Islands of the separation scope (native mn_islands_find, mn_islands_remove_below). Standing stock
// above the reach floor (the never-cut cells and the terraces of the trench) falls into 8-connected
// components; the innermost trench band lies at the floor and separates them. A component that
// touches the grid border or a cell without stock is the outer frame, every other one is an island.
// Milling an island out hands its cells back to the level masks and the coverage as the unrestricted
// plan has them, and nothing stands there.
public static class MaterialIslands
{
    public static unsafe IReadOnlyList<MaterialIsland> Find(HeightMap standing, HeightMap effectiveTip, HeightMap stock, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        ArgumentNullException.ThrowIfNull(stock);
        if (!standing.SameGridAs(effectiveTip) || !standing.SameGridAs(stock))
        {
            throw new ArgumentException("Standing, tip and stock maps must share the same grid.", nameof(standing));
        }

        var grid = CoreNative.GridOf(standing);
        int* cells = null;
        int* offsets = null;
        float* volumes = null;
        int count;
        fixed (float* s = standing.Z, t = effectiveTip.Z, k = stock.Z)
        {
            CoreNative.Check(CoreNative.mn_islands_find(&grid, s, t, k, tolerance, &cells, &offsets, &volumes, &count));
        }

        try
        {
            return IslandsOf(cells, offsets, volumes, count);
        }
        finally
        {
            CoreNative.mn_free(cells);
            CoreNative.mn_free(offsets);
            CoreNative.mn_free(volumes);
        }
    }

    // Gives the cells of every island below the threshold back to the masks, the coverage and the
    // standing map; returns the islands milled out.
    public static unsafe IReadOnlyList<MaterialIsland> RemoveBelow(
        IReadOnlyList<MaterialIsland> islands, float minVolume, IReadOnlyList<bool[,]> allowed, bool[][,] masks, bool[,] fullCoverage, bool[,] coverage, HeightMap standing)
    {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(fullCoverage);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(standing);
        if (!(minVolume >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(minVolume), minVolume, "The island volume to keep must be 0 or greater.");
        }

        var width = standing.Width;
        var height = standing.Height;
        var cellCount = standing.CellCount;
        var levels = masks.Length;
        var allowedBytes = new byte[Math.Max(levels * cellCount, 1)];
        var maskBytes = new byte[allowedBytes.Length];
        for (var k = 0; k < levels; k++)
        {
            CoreNative.Bytes(allowed[k]).CopyTo(allowedBytes, k * cellCount);
            CoreNative.Bytes(masks[k]).CopyTo(maskBytes, k * cellCount);
        }

        var full = CoreNative.Bytes(fullCoverage);
        var covered = CoreNative.Bytes(coverage);
        var islandCells = islands.SelectMany(i => i.Cells).ToArray();
        var islandOffsets = new int[islands.Count + 1];
        for (var k = 0; k < islands.Count; k++)
        {
            islandOffsets[k + 1] = islandOffsets[k] + islands[k].Cells.Length;
        }

        var islandVolumes = islands.Select(i => i.Volume).ToArray();
        var removed = new int[Math.Max(islands.Count, 1)];
        int removedCount;
        fixed (int* c = islandCells, o = islandOffsets, r = removed)
        fixed (float* v = islandVolumes, s = standing.Z)
        fixed (byte* a = allowedBytes, m = maskBytes, f = full, cov = covered)
        {
            CoreNative.Check(CoreNative.mn_islands_remove_below(c, o, v, islands.Count, minVolume, levels, cellCount, a, m, f, cov, s, r, &removedCount));
        }

        for (var k = 0; k < levels; k++)
        {
            CoreNative.CopyInto(maskBytes.AsSpan(k * cellCount, cellCount), masks[k]);
        }

        CoreNative.CopyInto(covered, coverage);
        return removed.Take(removedCount).Select(k => islands[k]).ToList();
    }

    internal static unsafe IReadOnlyList<MaterialIsland> IslandsOf(int* cells, int* offsets, float* volumes, int count)
    {
        var islands = new List<MaterialIsland>(count);
        for (var k = 0; k < count; k++)
        {
            islands.Add(new MaterialIsland(new ReadOnlySpan<int>(cells + offsets[k], offsets[k + 1] - offsets[k]).ToArray(), volumes[k]));
        }

        return islands;
    }
}
