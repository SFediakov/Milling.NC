using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// T-157: a single step never goes deeper than the cutter length, so the far block steps by the cutter
// length; every step runs over the positions whose head clears what stands (the material no far
// footprint covers, plus what the shallower steps leave), the walk cuts the rest.
public sealed class FarStepdownStepTests
{
    private static readonly ZLayerByLayerStrategy Strategy = new();

    private const float Cell = 0.5f;

    private const float CutterLength = 2f;

    private static ToolDefinition Cylinder(float headDiameter)
        => new() { CutterDiameter = 6f, CutterLength = CutterLength, HeadDiameter = headDiameter };

    private static ToolDefinition Frustum()
        => new() { CutterDiameter = 6f, CutterLength = CutterLength, HeadShape = HeadShape.Frustum, HeadDiameter = 7f, HeadTopDiameter = 20f, HeadLength = 10f };

    // A 10 x 10 x 2 box on the floor of a 40 x 40 x 9 stock: levels 7, 5, 3, 1, 0; far stepdown 8 with a
    // 2 mm cutter and a 2 mm stepdown gives steps of one level each at 7, 5, 3 and 1. The box is lower
    // than the cutter length, so the walls never block the head; what stands beside the far block is
    // the near band and the stock column over the box, at the stock top.
    private static ToolpathContext Context(ToolDefinition tool, float farStepdown)
    {
        var parameters = TestContexts.Parameters();
        parameters.FarStepdown = farStepdown;
        return TestContexts.Build(
            TestMeshes.Box(10, 10, 2),
            new StockDefinition { Shape = StockShape.Box, SizeX = 40, SizeY = 40, SizeZ = 9, AlignZ = StockAlignment.Min },
            tool,
            parameters);
    }

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

    // The first cutting move back at the first level after a deeper one: where the walk begins.
    private static int EndOfFarBlock(Toolpath path, float firstLevel)
    {
        var deeper = false;
        for (var k = 0; k < path.Count; k++)
        {
            var s = path.Segments[k];
            if (s.Kind == MoveKind.Rapid)
            {
                continue;
            }

            if (s.End.Z < firstLevel - 1e-3f)
            {
                deeper = true;
            }
            else if (deeper && MathF.Abs(s.End.Z - firstLevel) < 1e-3f)
            {
                return k;
            }
        }

        return path.Count;
    }

    private static float HeadRadiusAt(ToolDefinition tool, float h)
    {
        if (tool.HeadShape != HeadShape.Frustum || !(tool.HeadTopDiameter > tool.HeadDiameter) || h >= tool.HeadLength)
        {
            return tool.HeadRadius;
        }

        var bottom = tool.HeadDiameter / 2;
        var top = tool.HeadTopDiameter / 2;
        return bottom + (top - bottom) * MathF.Max(0f, h) / tool.HeadLength;
    }

    // The thresholds of the rule: 0 within the cutter length, else the head radius at the slab height
    // plus the margin, and at least what every shallower step two or more steps up demands.
    private static float[] Thresholds(ToolDefinition tool, float[] depths, float margin)
    {
        var c = tool.CutterLength;
        var r = tool.CutterRadius;
        var thresholds = new float[depths.Length];
        for (var t = 0; t < depths.Length; t++)
        {
            var value = 0f;
            if (depths[t] > c + Slicer.LevelTolerance)
            {
                value = HeadRadiusAt(tool, depths[t] - c) + margin;
                for (var u = 0; u + 1 < t; u++)
                {
                    var slab = depths[t] - depths[u] - c;
                    if (slab > Slicer.LevelTolerance)
                    {
                        value = MathF.Max(value, thresholds[u + 1] + r + HeadRadiusAt(tool, slab) + margin);
                    }
                }
            }

            thresholds[t] = value;
        }

        return thresholds;
    }

    // Far positions of a level: mask cells at least the stepover away from a cell with a tip above it.
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

        var far = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                if (float.IsNaN(tip[i, j]) || tip[i, j] > level + Slicer.LevelTolerance || !(stock[i, j] > level))
                {
                    continue;
                }

                var nearest = float.PositiveInfinity;
                foreach (var (oi, oj) in obstacles)
                {
                    nearest = MathF.Min(nearest, ((i - oi) * (i - oi) + (j - oj) * (j - oj)) * Cell * Cell);
                }

                far[i, j] = MathF.Sqrt(nearest) + Slicer.LevelTolerance >= context.Parameters.Stepover;
            }
        }

        return far;
    }

    // Distance of every cell to the material no far footprint covers.
    private static float[,] DistanceToStanding(ToolpathContext context, bool[,] far)
    {
        var w = far.GetLength(0);
        var h = far.GetLength(1);
        var covered = new bool[w, h];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                if (!far[i, j])
                {
                    continue;
                }

                foreach (var o in context.Profile.Offsets)
                {
                    var ii = i + o.Dx;
                    var jj = j + o.Dy;
                    if (ii >= 0 && jj >= 0 && ii < w && jj < h)
                    {
                        covered[ii, jj] = true;
                    }
                }
            }
        }

        var standing = new List<(int I, int J)>();
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                if (!float.IsNaN(context.Stock[i, j]) && !covered[i, j])
                {
                    standing.Add((i, j));
                }
            }
        }

        var distance = new float[w, h];
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var nearest = float.PositiveInfinity;
                foreach (var (si, sj) in standing)
                {
                    nearest = MathF.Min(nearest, ((i - si) * (i - si) + (j - sj) * (j - sj)) * Cell * Cell);
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

    private static int CellsAtOrBelow(HeightMap stock, float level, float tolerance)
    {
        var count = 0;
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                if (stock[i, j] <= level + tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void Cylinder_FarBlockStepsByTheCutterLength_OverShrinkingRegions()
    {
        var tool = Cylinder(10f);
        var context = Context(tool, 8f);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        Assert.Equal(new[] { 7f, 5f, 3f, 1f, 0f }, levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        // Steps at 7, 5 and 3 (the fourth threshold lies beyond the stock), then the walk 7, 5, 3, 1 and the
        // last group at 0. Travels between the corner patches of step 3 feed over the step 2 floor at 5, so
        // the order of the first cuts and of the walk's return to 7 is asserted, not every entry.
        var sequence = LevelSequence(path, levels);
        var order = $"levels {string.Join(", ", sequence)}";
        Assert.True(sequence[0] == 7f && sequence[1] == 5f && sequence[2] == 3f, order);
        Assert.True(sequence.IndexOf(3f) < sequence.LastIndexOf(7f), order);
        Assert.True(sequence.LastIndexOf(7f) < sequence.IndexOf(1f), order);
        Assert.True(sequence.IndexOf(1f) < sequence.IndexOf(0f), order);
        Assert.True(sequence.Skip(sequence.LastIndexOf(7f)).SequenceEqual(new[] { 7f, 5f, 3f, 1f, 0f }), order);

        var depths = new[] { 2f, 4f, 6f, 8f };
        var margin = MathF.Sqrt(2f) * Cell + context.Parameters.Tolerance;
        var thresholds = Thresholds(tool, depths, margin);
        Assert.Equal(0f, thresholds[0]);
        Assert.Equal(5f + margin, thresholds[1], 4);
        Assert.Equal(thresholds[1] + 3f + 5f + margin, thresholds[2], 4);
        Assert.Equal(thresholds[2] + 3f + 5f + margin, thresholds[3], 4);

        var far = FarPositions(context, 1f);
        var distance = DistanceToStanding(context, far);
        var stock = Simulate(context, path, EndOfFarBlock(path, 7f));
        var tolerance = context.Parameters.Tolerance;
        var counts = new int[4];
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                if (!far[i, j])
                {
                    continue;
                }

                var step = 0;
                for (var t = 0; t < thresholds.Length; t++)
                {
                    if (distance[i, j] >= thresholds[t])
                    {
                        step = t;
                    }
                }

                counts[step]++;
                var level = levels[step];
                Assert.True(stock[i, j] <= level + tolerance, $"position ({i}, {j}) at distance {distance[i, j]} stands at {stock[i, j]} above its step {level}");
                var next = step + 1 < thresholds.Length ? thresholds[step + 1] : float.PositiveInfinity;
                if (distance[i, j] < next - tool.CutterRadius - Cell)
                {
                    Assert.True(stock[i, j] >= level - tolerance, $"position ({i}, {j}) at distance {distance[i, j]} cut to {stock[i, j]} below its step {level}");
                }
            }
        }

        Assert.True(counts[0] > 0 && counts[1] > 0 && counts[2] > 0, $"steps hold {string.Join(", ", counts)} positions");
        Assert.Equal(0, counts[3]);
        Assert.Equal(0, CellsAtOrBelow(stock, 3f - tolerance - 1e-3f, 0f));

        var plain = Strategy.Generate(Context(tool, 0f), null, TestContext.Current.CancellationToken);
        AssertSameStock(Simulate(context, plain, plain.Count), Simulate(context, path, path.Count), tolerance);
    }

    [Fact]
    public void Frustum_UsesTheHeadRadiusAtTheSlabHeight_AndReachesFartherThanItsTopCylinder()
    {
        var frustum = Context(Frustum(), 8f);
        var top = Context(Cylinder(20f), 8f);
        var levels = frustum.Plan.Steps.Select(s => s.Level).ToList();
        var frustumPath = Strategy.Generate(frustum, null, TestContext.Current.CancellationToken);
        var topPath = Strategy.Generate(top, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(frustumPath, frustum.EffectiveTip, frustum.Parameters.Tolerance));
        Assert.Empty(GougeChecker.Verify(topPath, top.EffectiveTip, top.Parameters.Tolerance));

        // The frustum's narrow bottom lets the second step reach closer to the band and gives a third step
        // in the corners before the walk returns to 7; the 20 mm cylinder stops after the second step.
        var frustumSequence = LevelSequence(frustumPath, levels);
        var topSequence = LevelSequence(topPath, levels);
        Assert.Equal(new[] { 7f, 5f, 3f }, frustumSequence.Take(3));
        Assert.True(frustumSequence.IndexOf(3f) < frustumSequence.LastIndexOf(7f), string.Join(", ", frustumSequence));
        Assert.Equal(new[] { 7f, 5f, 7f }, topSequence.Take(3));
        Assert.True(topSequence.IndexOf(3f) > topSequence.LastIndexOf(7f), string.Join(", ", topSequence));
        var tolerance = frustum.Parameters.Tolerance;
        var frustumStock = Simulate(frustum, frustumPath, EndOfFarBlock(frustumPath, 7f));
        var topStock = Simulate(top, topPath, EndOfFarBlock(topPath, 7f));
        Assert.True(CellsAtOrBelow(frustumStock, 5f, tolerance) > CellsAtOrBelow(topStock, 5f, tolerance));

        AssertSameStock(Simulate(frustum, Strategy.Generate(Context(Frustum(), 0f), null, TestContext.Current.CancellationToken), int.MaxValue), Simulate(frustum, frustumPath, frustumPath.Count), tolerance);
    }
}
