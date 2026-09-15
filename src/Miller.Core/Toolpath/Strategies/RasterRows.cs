namespace Miller.Core.Toolpaths.Strategies;

// Row bookkeeping shared by the raster strategies.
public static class RasterRows
{
    // Guards floor() against 3 / 0.5 evaluating to 5.9999995.
    private const float RowStepEpsilon = 1e-4f;

    public static int RowStepCells(float spacing, float cellSize)
        => Math.Max(1, (int)MathF.Floor(spacing / cellSize + RowStepEpsilon));

    // 0, rowStep, 2 rowStep, ... and always the last row, so no strip at the far edge is skipped.
    public static IEnumerable<int> RowIndices(int height, int rowStep)
    {
        var last = -1;
        for (var j = 0; j < height; j += rowStep)
        {
            last = j;
            yield return j;
        }

        if (last != height - 1)
        {
            yield return height - 1;
        }
    }

    // Runs of consecutive allowed cells as (first, last) in travel order.
    public static IEnumerable<(int First, int Last)> Runs(Func<int, bool> allowed, int width, bool forward)
    {
        var runs = new List<(int, int)>();
        var i = 0;
        while (i < width)
        {
            if (!allowed(i))
            {
                i++;
                continue;
            }

            var start = i;
            while (i + 1 < width && allowed(i + 1))
            {
                i++;
            }

            runs.Add((start, i));
            i++;
        }

        if (forward)
        {
            return runs;
        }

        runs.Reverse();
        return runs.Select(r => (r.Item2, r.Item1));
    }
}
