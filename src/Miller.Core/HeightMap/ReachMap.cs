using Miller.Core.Native;
using Miller.Core.Progress;
using Miller.Core.Slicing;

namespace Miller.Core.HeightMaps;

// Where the tool axis may go, decided in rounds by a vote over its footprint (guide 6.3; native
// mn_reach_map). At a level z a footprint cell that holds stock is "unintended" when the model stands
// above the tool bottom there (model - dz > z) and "intended" otherwise; a position is reachable at z
// when the intended cells that still carry material are at least `percent` of those cells plus the
// unintended ones. Three rounds run at 100, percent + (100 - percent) / 2 and percent: the first is
// the drop cutter (no unintended cell ever), each later one admits more of them. After a round every
// footprint cell an accepted position brings to the model top within `tolerance` is marked reached and
// stops counting as intended in the later rounds, so a position gains nothing from cells that cleaner
// positions already finished; unintended cells always count, reached or not. Lowering z only turns
// intended cells into unintended ones, so reachability is monotone within a round, one height per
// position describes it and the map keeps the lowest floor over the rounds, never below the stock
// floor. Positions whose footprint touches no stock are NaN. Rows of a block run in parallel.
public static class ReachMap
{
    public const float DefaultPercent = 50f;
    public const float MinPercent = 0f;
    public const float MaxPercent = 100f;
    public const int Rounds = 3;

    // Rows computed together before a progress report; the rows of a block run in parallel.
    public const int RowsPerBlock = 16;

    public static unsafe HeightMap Compute(HeightMap model, HeightMap stock, ToolProfile profile, float floor, float percent = DefaultPercent, float tolerance = Slicer.LevelTolerance, IProgress<StepProgress>? progress = null)
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

        var reach = CoreNative.Empty(model, float.NaN);
        var grid = CoreNative.GridOf(model);
        var offsets = CoreNative.OffsetsOf(profile.Offsets);
        using var callback = new CoreNative.Progress(progress);
        fixed (float* m = model.Z, s = stock.Z, r = reach.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.Check(CoreNative.mn_reach_map(&grid, m, s, o, offsets.Length, floor, percent, tolerance, callback.Pointer, IntPtr.Zero, r));
        }

        return reach;
    }

    // Percent of the round (0-based): the drop cutter first, then halfway to the user's percent, then
    // the user's percent; in double so the vote's ceiling sees the same product as Rank.
    public static double RoundPercent(float percent, int round) => CoreNative.mn_reach_round_percent(percent, round);

    // Ascending rank of the reach floor among n values: the lowest value that still leaves at least
    // ceil(n * percent / 100) cells intended (values at or below it). 50 gives the median rule
    // n - (n / 2 + 1), 100 gives the largest value.
    public static int Rank(int n, float percent)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "At least one value is needed.");
        }

        return CoreNative.mn_reach_rank(n, percent);
    }

    // The lowest value at which the vote passes: the values at or below it that are not excluded
    // are at least `percent` of them plus every value above it (excluded values above it count,
    // excluded values at or below it count for neither side). Both spans are reordered together.
    public static unsafe float SelectThreshold(Span<float> values, Span<bool> excluded, double percent)
    {
        if (values.Length < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(values), values.Length, "At least one value is needed.");
        }

        if (excluded.Length != values.Length)
        {
            throw new ArgumentException("One flag per value is needed.", nameof(excluded));
        }

        var flags = new byte[excluded.Length];
        for (var k = 0; k < flags.Length; k++)
        {
            flags[k] = excluded[k] ? (byte)1 : (byte)0;
        }

        float result;
        fixed (float* v = values)
        fixed (byte* f = flags)
        {
            result = CoreNative.mn_reach_select_threshold(v, f, values.Length, percent);
        }

        for (var k = 0; k < flags.Length; k++)
        {
            excluded[k] = flags[k] != 0;
        }

        return result;
    }

    // The value that sorting ascending would put at `rank` (quickselect; the span is reordered).
    public static unsafe float Select(Span<float> values, int rank)
    {
        if (rank < 0 || rank >= values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank, $"Rank must lie in 0 .. {values.Length - 1}.");
        }

        fixed (float* v = values)
        {
            return CoreNative.mn_reach_select(v, values.Length, rank);
        }
    }

    // True where the tool may sit at the level: reach floor at or below it, with the slicer's slack.
    public static bool ReachableAt(float reachFloor, float level) => CoreNative.mn_reachable_at(reachFloor, level) != 0;
}
