using Miller.Core.HeightMaps;
using Miller.Core.Setup;

namespace Miller.Core.Slicing;

// Decomposes the job into steps. Roughing levels are z_k = stockTop - k * Stepdown while z_k is above
// the lowest effective tip; the last level is clamped to that lowest tip. A cell takes part in a
// roughing level when the cutter may sit at that level there (effectiveTip <= level) and the stock
// still has material above it (stock > level). One finishing step always follows.
public static class Slicer
{
    public static SlicePlan Build(HeightMap effectiveTip, HeightMap stock, CuttingParameters parameters)
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

        var stockTop = stock.Max();
        var lowest = effectiveTip.Min();
        if (float.IsNaN(stockTop) || float.IsNaN(lowest))
        {
            throw new ArgumentException("Stock or tip map holds no material at all.", nameof(stock));
        }

        var steps = new List<MillingStep>();
        foreach (var level in RoughingLevels(stockTop, lowest, parameters.Stepdown))
        {
            steps.Add(new MillingStep(level, MillingOperation.Roughing, RoughingMask(effectiveTip, stock, level)));
        }

        steps.Add(new MillingStep(lowest, MillingOperation.Finishing, MaterialMask(effectiveTip)));
        return new SlicePlan(steps, lowest);
    }

    // Float slack so a value sitting on a level is not lifted to the level above.
    public const float LevelTolerance = 1e-4f;

    // Material is removed level by level, so a surface at z stands at the lowest roughing level that
    // is still at or above z until the pass that reaches z; z above the first level stays at the top.
    public static float CeilToLevel(float z, float stockTop, float stepdown)
    {
        if (!(stepdown > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stepdown), stepdown, "Stepdown must be positive.");
        }

        if (float.IsNaN(z) || z >= stockTop)
        {
            return z;
        }

        var steps = MathF.Floor((stockTop - z) / stepdown + LevelTolerance);
        return stockTop - steps * stepdown;
    }

    public static HeightMap CeilToLevels(HeightMap map, float stockTop, float stepdown)
    {
        ArgumentNullException.ThrowIfNull(map);
        var result = map.Clone();
        for (var k = 0; k < result.Z.Length; k++)
        {
            result.Z[k] = CeilToLevel(result.Z[k], stockTop, stepdown);
        }

        return result;
    }

    public static IEnumerable<float> RoughingLevels(float stockTop, float lowest, float stepdown)
    {
        for (var k = 1; ; k++)
        {
            var level = stockTop - k * stepdown;
            if (level <= lowest)
            {
                if (lowest < stockTop)
                {
                    yield return lowest;
                }

                yield break;
            }

            yield return level;
        }
    }

    private static bool[,] RoughingMask(HeightMap tip, HeightMap stock, float level)
    {
        var mask = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                var t = tip[i, j];
                var s = stock[i, j];
                mask[i, j] = !float.IsNaN(t) && !float.IsNaN(s) && t <= level && s > level;
            }
        }

        return mask;
    }

    private static bool[,] MaterialMask(HeightMap tip)
    {
        var mask = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                mask[i, j] = !float.IsNaN(tip[i, j]);
            }
        }

        return mask;
    }
}
