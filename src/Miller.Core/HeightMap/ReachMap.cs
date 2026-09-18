using Miller.Core.Progress;
using Miller.Core.Slicing;

namespace Miller.Core.HeightMaps;

// Where the tool axis may go, decided in rounds by a vote over its footprint (guide 6.3). At a level
// z a footprint cell that holds stock is "unintended" when the model stands above the tool bottom
// there (model - dz > z) and "intended" otherwise; a position is reachable at z when the intended
// cells that still carry material are at least `percent` of those cells plus the unintended ones.
// Three rounds run at 100, percent + (100 - percent) / 2 and percent: the first is the drop cutter
// (no unintended cell ever), each later one admits more of them. After a round every footprint cell
// an accepted position brings to the model top within `tolerance` is marked reached and stops
// counting as intended in the later rounds, so a position gains nothing from cells that cleaner
// positions already finished; unintended cells always count, reached or not. Lowering z only turns
// intended cells into unintended ones, so reachability is monotone within a round, one height per
// position describes it and the map keeps the lowest floor over the rounds, never below the stock
// floor (a ball bottom stands above the tip at the footprint edge, so the values there can lie under
// the floor). Positions whose footprint touches no stock are NaN.
public static class ReachMap
{
    public const float DefaultPercent = 50f;
    public const float MinPercent = 0f;
    public const float MaxPercent = 100f;
    public const int Rounds = 3;

    // Rows computed together before a progress report; the rows of a block run in parallel.
    public const int RowsPerBlock = 16;

    // Guards ceil(n * p / 100) against 2.0000000004 for exact products such as 4 * 50 / 100.
    private const double RankSlack = 1e-9;

    public static HeightMap Compute(HeightMap model, HeightMap stock, ToolProfile profile, float floor, float percent = DefaultPercent, float tolerance = Slicer.LevelTolerance, IProgress<StepProgress>? progress = null)
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

        if (!(tolerance >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be zero or positive.");
        }

        var offsets = profile.Offsets;
        var width = model.Width;
        var height = model.Height;
        var reach = new HeightMap(model.OriginX, model.OriginY, model.CellSize, width, height, float.NaN);
        // The value every cell votes with before the tool bottom offset: the model top, the floor
        // where the model is NaN, NaN where the stock is NaN so the cell is skipped.
        var value = new float[model.CellCount];
        for (var k = 0; k < value.Length; k++)
        {
            var top = model.Z[k];
            value[k] = float.IsNaN(stock.Z[k]) ? float.NaN : float.IsNaN(top) ? floor : top;
        }

        var reached = new bool[model.CellCount];
        // Marks of the running round; merged into `reached` after it so the scan order has no effect.
        var reachedNext = new bool[model.CellCount];
        var span = new int[offsets.Length];
        var dz = new float[offsets.Length];
        var margin = 0;
        for (var o = 0; o < offsets.Length; o++)
        {
            span[o] = offsets[o].Dy * width + offsets[o].Dx;
            dz[o] = offsets[o].Dz;
            margin = Math.Max(margin, Math.Max(Math.Abs(offsets[o].Dx), Math.Abs(offsets[o].Dy)));
        }

        for (var round = 0; round < Rounds; round++)
        {
            var roundPercent = RoundPercent(percent, round);
            // Marks serve the next round only, so the last round leaves none.
            var lastRound = round == Rounds - 1;
            // The same percent with more cells excluded passes nowhere the previous round did not
            // (a percent of 100 repeats the drop cutter), so such a round is over at once.
            if (round > 0 && roundPercent == RoundPercent(percent, round - 1))
            {
                progress?.Report(new StepProgress(round + 1, Rounds, (round + 1f) / Rounds));
                continue;
            }

            // The rows of a block run in parallel: a row writes the reach cells of its own row only,
            // reads maps no row writes in this round, and its marks store true into reachedNext,
            // which needs no order. The block loop stays sequential so the reports arrive in order.
            for (var first = 0; first < height; first += RowsPerBlock)
            {
                var last = Math.Min(first + RowsPerBlock, height);
                Parallel.For(first, last, () => new Scratch(offsets.Length), (j, _, scratch) =>
                {
                    Row(j, scratch, roundPercent, lastRound);
                    return scratch;
                }, _ => { });
                progress?.Report(new StepProgress(round + 1, Rounds, (round + (float)last / height) / Rounds));
            }

            if (!lastRound)
            {
                Array.Copy(reachedNext, reached, reached.Length);
            }
        }

        return reach;

        void Row(int j, Scratch scratch, double roundPercent, bool lastRound)
        {
            var tops = scratch.Tops;
            var cells = scratch.Cells;
            var flags = scratch.Flags;
            var interiorRow = j >= margin && j < height - margin;
            for (var i = 0; i < width; i++)
            {
                var n = 0;
                var excluded = 0;
                var center = j * width + i;
                if (interiorRow && i >= margin && i < width - margin)
                {
                    for (var o = 0; o < span.Length; o++)
                    {
                        var k = center + span[o];
                        var top = value[k];
                        if (float.IsNaN(top))
                        {
                            continue;
                        }

                        var done = reached[k];
                        tops[n] = top - dz[o];
                        cells[n] = k;
                        flags[n] = done;
                        excluded += done ? 1 : 0;
                        n++;
                    }
                }
                else
                {
                    foreach (var o in offsets)
                    {
                        var ii = i + o.Dx;
                        var jj = j + o.Dy;
                        if (!model.InBounds(ii, jj))
                        {
                            continue;
                        }

                        var k = jj * width + ii;
                        var top = value[k];
                        if (float.IsNaN(top))
                        {
                            continue;
                        }

                        var done = reached[k];
                        tops[n] = top - o.Dz;
                        cells[n] = k;
                        flags[n] = done;
                        excluded += done ? 1 : 0;
                        n++;
                    }
                }

                // No stock under the footprint stays NaN; a footprint finished by earlier rounds
                // has nothing left to gain and keeps its floor.
                if (n == 0 || excluded == n)
                {
                    continue;
                }

                var z = MathF.Max(Threshold(tops.AsSpan(0, n), flags.AsSpan(0, n), excluded, roundPercent, scratch.Work), floor);
                var previous = reach.Z[center];
                // An unchanged floor marked the same cells in the round before.
                if (!(float.IsNaN(previous) || z < previous))
                {
                    continue;
                }

                reach.Z[center] = z;
                if (lastRound)
                {
                    continue;
                }

                var touched = z - tolerance;
                for (var m = 0; m < n; m++)
                {
                    if (tops[m] >= touched)
                    {
                        reachedNext[cells[m]] = true;
                    }
                }
            }
        }
    }

    // Per-worker buffers of one footprint: the gathered values and their cells and flags in gather
    // order, and the copy the selection reorders.
    private sealed class Scratch
    {
        public Scratch(int size)
        {
            Tops = new float[size];
            Cells = new int[size];
            Flags = new bool[size];
            Work = new float[size];
        }

        public float[] Tops { get; }

        public int[] Cells { get; }

        public bool[] Flags { get; }

        public float[] Work { get; }
    }

    // The round floor of one footprint. SelectThreshold answers every case; the two cheaper forms
    // give the same value where they apply (100 percent is the largest value, and with nothing
    // excluded the passing rank is Rank), which the rounds tests pin. `work` receives the copy that
    // the selection reorders, so `values` keeps the gather order for the reached marks.
    private static float Threshold(ReadOnlySpan<float> values, Span<bool> flags, int excluded, double percent, float[] work)
    {
        var n = values.Length;
        if (percent >= MaxPercent)
        {
            var max = values[0];
            for (var m = 1; m < n; m++)
            {
                max = MathF.Max(max, values[m]);
            }

            return max;
        }

        values.CopyTo(work);
        return excluded == 0
            ? Select(work.AsSpan(0, n), Math.Clamp(Needed(n, percent) - 1, 0, n - 1))
            : SelectThreshold(work.AsSpan(0, n), flags, percent);
    }

    // Percent of the round (0-based): the drop cutter first, then halfway to the user's percent, then
    // the user's percent; in double so the vote's ceiling sees the same product as Rank.
    public static double RoundPercent(float percent, int round) => round switch
    {
        0 => MaxPercent,
        1 => percent + (MaxPercent - (double)percent) / 2.0,
        _ => percent,
    };

    // Ascending rank of the reach floor among n values: the lowest value that still leaves at least
    // ceil(n * percent / 100) cells intended (values at or below it). 50 gives the median rule
    // n - (n / 2 + 1), 100 gives the largest value.
    public static int Rank(int n, float percent)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "At least one value is needed.");
        }

        return Math.Clamp(Needed(n, percent) - 1, 0, n - 1);
    }

    private static int Needed(int n, double percent) => (int)Math.Ceiling(n * percent / 100.0 - RankSlack);

    // The lowest value at which the vote passes: the values at or below it that are not excluded
    // are at least `percent` of them plus every value above it (excluded values above it count,
    // excluded values at or below it count for neither side). Passing is monotone in the rank, so
    // the rank range narrows the way Select does; the flags travel with the values and both spans
    // are reordered. With nothing excluded this is Select at Rank(n, percent).
    public static float SelectThreshold(Span<float> values, Span<bool> excluded, double percent)
    {
        if (values.Length < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(values), values.Length, "At least one value is needed.");
        }

        if (excluded.Length != values.Length)
        {
            throw new ArgumentException("One flag per value is needed.", nameof(excluded));
        }

        var n = values.Length;
        var lo = 0;
        var hi = n - 1;
        // Excluded values among the ranks below lo; every rank below lo fails, rank hi passes.
        var excludedBelow = 0;
        while (lo < hi)
        {
            var pivot = values[(lo + hi) / 2];
            var l = lo;
            var h = hi;
            // Every value the left scan passes ends left of l, so the excluded ones are counted there.
            var e = excludedBelow;
            while (l <= h)
            {
                while (values[l] < pivot)
                {
                    e += excluded[l] ? 1 : 0;
                    l++;
                }

                while (values[h] > pivot)
                {
                    h--;
                }

                if (l <= h)
                {
                    (values[l], values[h]) = (values[h], values[l]);
                    (excluded[l], excluded[h]) = (excluded[h], excluded[l]);
                    e += excluded[l] ? 1 : 0;
                    l++;
                    h--;
                }
            }

            // values[lo..h] <= pivot <= values[l..hi]; when l == h + 2 the value between is the pivot
            // and was passed by the left scan, so it is taken out of the count for rank h.
            var middle = l == h + 2;
            var eLeft = middle ? e - (excluded[h + 1] ? 1 : 0) : e;
            if (h >= lo && Passes(h, eLeft, n, percent))
            {
                hi = h;
                continue;
            }

            excludedBelow = eLeft;
            if (middle)
            {
                if (Passes(h + 1, e, n, percent))
                {
                    return values[h + 1];
                }

                excludedBelow = e;
            }

            lo = l;
        }

        return values[lo];
    }

    // The value at ascending `rank` passes when the values at or below it that are not excluded
    // (rank + 1 minus the excluded ones among them) reach the needed share of the values that count.
    private static bool Passes(int rank, int excludedUpTo, int n, double percent)
        => rank + 1 - excludedUpTo >= Needed(n - excludedUpTo, percent);

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
