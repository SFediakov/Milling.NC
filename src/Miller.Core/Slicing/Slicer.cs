using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Setup;

namespace Miller.Core.Slicing;

// Decomposes the job into levels z_k = stockTop - k * Stepdown while z_k is above the lowest
// effective tip; the last level is clamped to that lowest tip (native mn_slice). A cell takes part in
// a level when the tool may sit at that level there (effectiveTip <= level, with LevelTolerance) and
// the stock still has material above it (stock > level). The coverage mask holds every cell with an
// effective tip.
public static class Slicer
{
    // Float slack so a value sitting on a level is not lifted to the level above; also the slack of
    // the level masks (a rasterized 30 top reads 30.000002).
    public const float LevelTolerance = 1e-4f;

    public static unsafe SlicePlan Build(HeightMap effectiveTip, HeightMap stock, CuttingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!effectiveTip.SameGridAs(stock))
        {
            throw new ArgumentException("Tip map and stock map must share the same grid.", nameof(stock));
        }

        if (!(parameters.Stepdown > 0))
        {
            throw new ArgumentException($"Stepdown must be positive, got {parameters.Stepdown}.", nameof(parameters));
        }

        var grid = CoreNative.GridOf(effectiveTip);
        IntPtr plan;
        fixed (float* t = effectiveTip.Z, s = stock.Z)
        {
            var status = CoreNative.mn_slice(&grid, t, s, parameters.Stepdown, &plan);
            if (status == CoreNative.ErrorArgument)
            {
                throw new ArgumentException(CoreNative.LastError, nameof(stock));
            }

            CoreNative.Check(status);
        }

        try
        {
            return CoreNative.PlanOf(plan, effectiveTip.Width, effectiveTip.Height);
        }
        finally
        {
            CoreNative.mn_plan_free(plan);
        }
    }

    // Material is removed level by level, so a surface at z stands at the lowest level that
    // is still at or above z until the pass that reaches z; z above the first level stays at the top.
    public static float CeilToLevel(float z, float stockTop, float stepdown)
    {
        if (!(stepdown > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stepdown), stepdown, "Stepdown must be positive.");
        }

        return CoreNative.mn_ceil_to_level(z, stockTop, stepdown);
    }

    public static unsafe HeightMap CeilToLevels(HeightMap map, float stockTop, float stepdown)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!(stepdown > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stepdown), stepdown, "Stepdown must be positive.");
        }

        var result = CoreNative.Empty(map, float.NaN);
        fixed (float* source = map.Z, target = result.Z)
        {
            CoreNative.mn_ceil_to_levels(source, map.CellCount, stockTop, stepdown, target);
        }

        return result;
    }

    public static unsafe IEnumerable<float> Levels(float stockTop, float lowest, float stepdown)
    {
        if (!(stepdown > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stepdown), stepdown, "Stepdown must be positive.");
        }

        float* levels = null;
        int count;
        CoreNative.Check(CoreNative.mn_levels(stockTop, lowest, stepdown, &levels, &count));
        try
        {
            return new ReadOnlySpan<float>(levels, count).ToArray();
        }
        finally
        {
            CoreNative.mn_free(levels);
        }
    }
}
