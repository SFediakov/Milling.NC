using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// Far stepdown (T-156) in "Z layer by layer": per group of k levels the far region of the bottom level
// (positions at least the stepover away from everything that stays above it) is cut first, k levels in
// one step, then the near band runs through the normal levels, and the next group starts where the far
// pass ended. The final stock is the one of the plain strategy.
public sealed class FarStepdownStrategyTests
{
    private static readonly ZLayerByLayerStrategy Strategy = new();

    private const float Cell = 0.5f;

    private static CuttingParameters Parameters(float farStepdown)
    {
        var parameters = TestContexts.Parameters();
        parameters.FarStepdown = farStepdown;
        return parameters;
    }

    // A 10 x 10 box of the stock height centred in a 40 x 40 stock: the ring round it is 15 mm wide;
    // with a 3 mm stepover under the 6 mm cutter the near positions lie within 6 mm of the box wall.
    private static ToolpathContext Context(float stockHeight, float farStepdown)
        => TestContexts.Build(
            TestMeshes.Box(10, 10, stockHeight),
            new StockDefinition { Shape = StockShape.Box, SizeX = 40, SizeY = 40, SizeZ = stockHeight },
            TestContexts.FlatTool6(),
            Parameters(farStepdown));

    // The levels the cutting moves pass through, consecutive repeats collapsed.
    private static List<float> LevelSequence(Toolpath path, IReadOnlyList<float> levels)
    {
        var sequence = new List<float>();
        foreach (var s in path.Segments)
        {
            if (s.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var level = levels.FirstOrDefault(l => MathF.Abs(l - s.End.Z) < 1e-3f, float.NaN);
            if (!float.IsNaN(level) && (sequence.Count == 0 || sequence[^1] != level))
            {
                sequence.Add(level);
            }
        }

        return sequence;
    }

    private static HeightMap Simulate(ToolpathContext context, Toolpath path, int segments)
    {
        var stock = context.Stock.Clone();
        new SimulationEngine(path, stock, context.Profile).SeekToSegment(segments);
        return stock;
    }

    private static int FirstCutAt(Toolpath path, float level)
        => path.Segments.ToList().FindIndex(s => s.Kind != MoveKind.Rapid && MathF.Abs(s.End.Z - level) < 1e-3f);

    // The first cutting move that goes below the level (a plunge, or a ramp vertex on the way down).
    private static int FirstCutBelow(Toolpath path, float level)
        => path.Segments.ToList().FindIndex(s => s.Kind != MoveKind.Rapid && s.End.Z < level - 1e-3f);

    // The far positions of a plan level by the rule: positions of the level whose distance to every
    // cell with an effective tip above the level is at least the stepover.
    private static bool[,] FarPositions(ToolpathContext context, float level)
    {
        var tip = context.EffectiveTip;
        var stock = context.Stock;
        var obstacles = new List<(int I, int J)>();
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                if (!float.IsNaN(tip[i, j]) && tip[i, j] > level + Slicer.LevelTolerance)
                {
                    obstacles.Add((i, j));
                }
            }
        }

        var stepover = context.Parameters.Stepover;
        var far = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                var inMask = !float.IsNaN(tip[i, j]) && tip[i, j] <= level + Slicer.LevelTolerance && stock[i, j] > level;
                if (!inMask)
                {
                    continue;
                }

                var nearest = float.PositiveInfinity;
                foreach (var (oi, oj) in obstacles)
                {
                    var d = ((i - oi) * (i - oi) + (j - oj) * (j - oj)) * Cell * Cell;
                    nearest = MathF.Min(nearest, d);
                }

                far[i, j] = MathF.Sqrt(nearest) + Slicer.LevelTolerance >= stepover;
            }
        }

        return far;
    }

    // Distance from a cell to the nearest far position, in mm.
    private static float[,] DistanceToFar(bool[,] far)
    {
        var w = far.GetLength(0);
        var h = far.GetLength(1);
        var positions = new List<(int I, int J)>();
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                if (far[i, j])
                {
                    positions.Add((i, j));
                }
            }
        }

        var distance = new float[w, h];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var nearest = float.PositiveInfinity;
                foreach (var (pi, pj) in positions)
                {
                    nearest = MathF.Min(nearest, ((i - pi) * (i - pi) + (j - pj) * (j - pj)) * Cell * Cell);
                }

                distance[i, j] = MathF.Sqrt(nearest);
            }
        }

        return distance;
    }

    private static void AssertSameStock(HeightMap expected, HeightMap actual, float tolerance)
    {
        for (var j = 0; j < expected.Height; j++)
        {
            for (var i = 0; i < expected.Width; i++)
            {
                Assert.True(MathF.Abs(expected[i, j] - actual[i, j]) <= tolerance, $"cell ({i}, {j}): {actual[i, j]} against {expected[i, j]}");
            }
        }
    }

    [Fact]
    public void FarPass_CutsTheFarAreaFirst_ThenTheNearBandLevelByLevel()
    {
        var context = Context(5f, 4f);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        Assert.Equal(new[] { 3f, 1f, 0f }, levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        Assert.Equal(new[] { 1f, 3f, 1f, 0f }, LevelSequence(path, levels));

        // After the far pass the material under the far positions is at level 1 and the band round
        // the box is untouched.
        var far = FarPositions(context, 1f);
        var distance = DistanceToFar(far);
        var radius = context.Tool.CutterDiameter / 2f;
        var firstNear = FirstCutAt(path, 3f);
        Assert.True(firstNear > 0);
        var afterFar = Simulate(context, path, firstNear);
        var tolerance = context.Parameters.Tolerance;
        var cut = 0;
        var kept = 0;
        for (var j = 0; j < afterFar.Height; j++)
        {
            for (var i = 0; i < afterFar.Width; i++)
            {
                if (distance[i, j] <= radius)
                {
                    Assert.True(afterFar[i, j] <= 1f + tolerance, $"far cell ({i}, {j}) at {afterFar[i, j]}");
                    cut++;
                }
                else if (distance[i, j] > radius + Cell)
                {
                    Assert.Equal(5f, afterFar[i, j], 3);
                    kept++;
                }
            }
        }

        // The box footprint (400 cells) and the near band round it stay at the top.
        Assert.True(cut > 1000, $"{cut} far cells");
        Assert.True(kept > 600, $"{kept} near cells");

        // The group ends where the plain strategy stands before its first move below level 1: the ring
        // at level 1; and both end at the same stock.
        var plain = Strategy.Generate(Context(5f, 0f), null, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 3f, 1f, 0f }, LevelSequence(plain, levels));
        AssertSameStock(Simulate(context, plain, FirstCutBelow(plain, 1f)), Simulate(context, path, FirstCutBelow(path, 1f)), tolerance);
        AssertSameStock(Simulate(context, plain, plain.Count), Simulate(context, path, path.Count), tolerance);
    }

    [Fact]
    public void SecondGroup_StartsWithItsFarPass_AndTheLastPartialGroupHasNone()
    {
        var context = Context(9f, 4f);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        Assert.Equal(new[] { 7f, 5f, 3f, 1f, 0f }, levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        Assert.Equal(new[] { 5f, 7f, 5f, 1f, 3f, 1f, 0f }, LevelSequence(path, levels));
        var plain = Strategy.Generate(Context(9f, 0f), null, TestContext.Current.CancellationToken);
        AssertSameStock(Simulate(context, plain, plain.Count), Simulate(context, path, path.Count), context.Parameters.Tolerance);
    }

    [Fact]
    public void FarStepdownThatIsNoMultiple_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Strategy.Generate(Context(5f, 3f), null, TestContext.Current.CancellationToken));
    }
}
