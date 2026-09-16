using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;

namespace Miller.Core.Slicing;

// One island: an 8-connected region of standing stock the trench encloses, its cells as flat
// indices (j * width + i) and the material milling it out would remove.
public sealed record MaterialIsland(int[] Cells, float Volume);

// Islands of the separation scope. Standing stock above the reach floor (the never-cut cells and
// the terraces of the trench) falls into 8-connected components; the innermost trench band lies at
// the floor and separates them. A component that touches the grid border or a cell without stock
// is the outer frame, every other one is an island. Milling an island out hands its cells back to
// the level masks and the coverage as the unrestricted plan has them, and nothing stands there.
public static class MaterialIslands
{
    public static IReadOnlyList<MaterialIsland> Find(HeightMap standing, HeightMap effectiveTip, HeightMap stock, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        ArgumentNullException.ThrowIfNull(stock);
        if (!standing.SameGridAs(effectiveTip) || !standing.SameGridAs(stock))
        {
            throw new ArgumentException("Standing, tip and stock maps must share the same grid.", nameof(standing));
        }

        var width = standing.Width;
        var height = standing.Height;
        var mask = new bool[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var above = standing[i, j] - effectiveTip[i, j];
                mask[i, j] = !float.IsNaN(stock[i, j]) && above > tolerance;
            }
        }

        CaveTree.Label(mask, width, height, out var components);
        var area = standing.CellSize * standing.CellSize;
        var islands = new List<MaterialIsland>();
        foreach (var cells in components)
        {
            if (TouchesTheOutside(cells, stock))
            {
                continue;
            }

            var volume = 0f;
            foreach (var c in cells)
            {
                volume += (standing.Z[c] - effectiveTip.Z[c]) * area;
            }

            islands.Add(new MaterialIsland(cells, volume));
        }

        return islands;
    }

    // Gives the cells of every island below the threshold back to the masks, the coverage and the
    // standing map; returns the islands milled out.
    public static IReadOnlyList<MaterialIsland> RemoveBelow(
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

        var removed = new List<MaterialIsland>();
        var width = standing.Width;
        foreach (var island in islands)
        {
            if (!(island.Volume < minVolume))
            {
                continue;
            }

            foreach (var c in island.Cells)
            {
                var i = c % width;
                var j = c / width;
                for (var k = 0; k < masks.Length; k++)
                {
                    masks[k][i, j] = allowed[k][i, j];
                }

                coverage[i, j] = fullCoverage[i, j];
                standing.Z[c] = float.NaN;
            }

            removed.Add(island);
        }

        return removed;
    }

    private static bool TouchesTheOutside(int[] cells, HeightMap stock)
    {
        foreach (var c in cells)
        {
            var i = c % stock.Width;
            var j = c / stock.Width;
            for (var dj = -1; dj <= 1; dj++)
            {
                for (var di = -1; di <= 1; di++)
                {
                    var ii = i + di;
                    var jj = j + dj;
                    if (!stock.InBounds(ii, jj) || float.IsNaN(stock[ii, jj]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
