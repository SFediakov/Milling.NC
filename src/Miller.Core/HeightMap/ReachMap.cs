using Miller.Core.Slicing;

namespace Miller.Core.HeightMaps;

// Where the tool axis may go, decided by a vote over its footprint (guide 6.3). At a level z a
// footprint cell that holds stock is "unintended" when the model stands above the tool bottom there
// (model - dz > z) and "intended" otherwise; a position is reachable at z when the intended cells
// are at least `percent` of the footprint cells that hold stock (50 = majority, ties to the stock;
// 100 = every cell, the drop cutter). Lowering z only turns intended cells into unintended ones, so
// reachability is monotone and one height per position, the reach floor, describes it: the value
// at ascending rank ceil(n * percent / 100) - 1 of the n values model - dz over the footprint cells
// that hold stock, never below the stock floor (a ball bottom stands above the tip at the footprint
// edge, so the values there can lie under the floor). Positions whose footprint touches no stock
// are NaN. Against the drop cutter (HeightMapDilation.ComputeTipMap, the largest of the n values)
// this cuts the minority cells of a mixed footprint on purpose.
public static class ReachMap
{
    public const float DefaultPercent = 50f;
    public const float MinPercent = 0f;
    public const float MaxPercent = 100f;

    // Guards ceil(n * p / 100) against 2.0000000004 for exact products such as 4 * 50 / 100.
    private const double RankSlack = 1e-9;

    public static HeightMap Compute(HeightMap model, HeightMap stock, ToolProfile profile, float floor, float percent = DefaultPercent)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        if (!model.SameGridAs(stock))
        {
            throw new ArgumentException("Model map and stock map must share the same grid.", nameof(stock));
        }

        if (!(percent > MinPercent && percent <= MaxPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, $"Reach percent must lie in ({MinPercent}, {MaxPercent}].");
        }

        var offsets = profile.Offsets;
        var width = model.Width;
        var height = model.Height;
        var reach = new HeightMap(model.OriginX, model.OriginY, model.CellSize, width, height, float.NaN);
        var modelZ = model.Z;
        var stockZ = stock.Z;
        var tops = new float[offsets.Length];
        var span = new int[offsets.Length];
        var dz = new float[offsets.Length];
        var margin = 0;
        for (var o = 0; o < offsets.Length; o++)
        {
            span[o] = offsets[o].Dy * width + offsets[o].Dx;
            dz[o] = offsets[o].Dz;
            margin = Math.Max(margin, Math.Max(Math.Abs(offsets[o].Dx), Math.Abs(offsets[o].Dy)));
        }

        for (var j = 0; j < height; j++)
        {
            var interiorRow = j >= margin && j < height - margin;
            for (var i = 0; i < width; i++)
            {
                var n = 0;
                var center = j * width + i;
                if (interiorRow && i >= margin && i < width - margin)
                {
                    for (var o = 0; o < span.Length; o++)
                    {
                        var k = center + span[o];
                        if (float.IsNaN(stockZ[k]))
                        {
                            continue;
                        }

                        var top = modelZ[k];
                        tops[n++] = (float.IsNaN(top) ? floor : top) - dz[o];
                    }
                }
                else
                {
                    foreach (var o in offsets)
                    {
                        var ii = i + o.Dx;
                        var jj = j + o.Dy;
                        if (!model.InBounds(ii, jj) || float.IsNaN(stock[ii, jj]))
                        {
                            continue;
                        }

                        var top = model[ii, jj];
                        tops[n++] = (float.IsNaN(top) ? floor : top) - o.Dz;
                    }
                }

                if (n == 0)
                {
                    continue;
                }

                reach.Z[center] = MathF.Max(Select(tops.AsSpan(0, n), Rank(n, percent)), floor);
            }
        }

        return reach;
    }

    // Ascending rank of the reach floor among n values: the lowest value that still leaves at least
    // ceil(n * percent / 100) cells intended (values at or below it). 50 gives the median rule
    // n - (n / 2 + 1), 100 gives the largest value.
    public static int Rank(int n, float percent)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "At least one value is needed.");
        }

        var needed = (int)Math.Ceiling(n * (double)percent / 100.0 - RankSlack);
        return Math.Clamp(needed - 1, 0, n - 1);
    }

    // The value that sorting ascending would put at `rank` (quickselect; the span is reordered).
    public static float Select(Span<float> values, int rank)
    {
        if (rank < 0 || rank >= values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, $"Rank must lie in 0 .. {values.Length - 1}.");
        }

        var lo = 0;
        var hi = values.Length - 1;
        while (lo < hi)
        {
            var pivot = values[(lo + hi) / 2];
            var l = lo;
            var h = hi;
            while (l <= h)
            {
                while (values[l] < pivot)
                {
                    l++;
                }

                while (values[h] > pivot)
                {
                    h--;
                }

                if (l <= h)
                {
                    (values[l], values[h]) = (values[h], values[l]);
                    l++;
                    h--;
                }
            }

            if (rank <= h)
            {
                hi = h;
            }
            else if (rank >= l)
            {
                lo = l;
            }
            else
            {
                break;
            }
        }

        return values[rank];
    }

    // True where the tool may sit at the level: reach floor at or below it, with the slicer's slack.
    public static bool ReachableAt(float reachFloor, float level) => !float.IsNaN(reachFloor) && reachFloor <= level + Slicer.LevelTolerance;
}
