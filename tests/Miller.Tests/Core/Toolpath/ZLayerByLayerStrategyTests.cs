using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// Cave by cave: every level of a cave before the next level, the whole subtree before a sibling,
// a rise only as high as the split level, and every reachable cell cut to its floor in the end.
public sealed class ZLayerByLayerStrategyTests
{
    private static readonly ZLayerByLayerStrategy Strategy = new();

    // 30 x 12 x 5 stock with a wall of the given height standing on its floor across the middle
    // (x from 10 to 20). With the 6 mm cutter the wall is cut back to x in 13 .. 17 and still
    // separates the two sides.
    private static ToolpathContext WallInStock(float wallHeight)
        => TestContexts.Build(
            TestMeshes.Box(10, 12, wallHeight),
            new StockDefinition { Shape = StockShape.Box, SizeX = 30, SizeY = 12, SizeZ = 5, AlignZ = StockAlignment.Min },
            TestContexts.FlatTool6(),
            TestContexts.Parameters());

    // The levels the cutting moves pass through, consecutive repeats collapsed; travel heights
    // (the wall top, safe Z) are not levels and do not appear.
    private static List<float> LevelSequence(Toolpath path, IEnumerable<float> levels)
    {
        var set = levels.ToList();
        var sequence = new List<float>();
        foreach (var s in path.Segments)
        {
            if (s.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var level = set.FirstOrDefault(l => MathF.Abs(l - s.End.Z) < 1e-3f, float.NaN);
            if (!float.IsNaN(level) && (sequence.Count == 0 || sequence[^1] != level))
            {
                sequence.Add(level);
            }
        }

        return sequence;
    }

    private static HeightMap Simulate(ToolpathContext context, Toolpath path)
    {
        var stock = context.Stock.Clone();
        new SimulationEngine(path, stock, context.Profile).RunToEnd(TestContext.Current.CancellationToken);
        return stock;
    }

    [Fact]
    public void TwoCaves_AreCutOneAfterTheOther_WithOneRiseBetween()
    {
        var context = WallInStock(5f);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        Assert.Equal(new[] { 3f, 1f, 0f }, levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        Assert.Equal(new[] { 3f, 1f, 0f, 3f, 1f, 0f }, LevelSequence(path, levels));
        Assert.Equal(MoveKind.Plunge, path.Segments[0].Kind);
        Assert.Equal(context.SafeZ, path.Segments[0].Start.Z);
        Assert.Equal(MoveKind.Rapid, path.Segments[^1].Kind);
        Assert.Equal(context.SafeZ, path.Segments[^1].End.Z);
    }

    [Fact]
    public void SplitCave_FinishesOneSubtreeBeforeTheOther_AndTravelsByTheFasterWay()
    {
        // A 3 mm wall: one cave at level 3 (the wall top is reachable), two below it.
        var context = WallInStock(3f);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        var tree = CaveTree.Build(context.Plan.Steps);
        var root = Assert.Single(tree.Roots);
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Single(c.Children));

        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        // The first sub-cave down to the floor, then the second one from level 1: no level 3 between them.
        Assert.Equal(new[] { 3f, 1f, 0f, 1f, 0f }, LevelSequence(path, levels));

        // Between the sub-caves the writer takes the faster of the climb over the wall top (feeds at 800 up
        // 3, over 10 and down 2 mm: about 0.0225 min) and the retract (rapids at 3000 up to safe Z and over,
        // then a 9 mm plunge at 800: about 0.019 min). Plunges run at the feed rate since T-215, so the
        // retract wins; with the former plunge rate of 200 the tool crossed at the wall top instead.
        var floorMoves = path.Segments.Select((s, k) => (s, k)).Where(p => p.s.Kind != MoveKind.Rapid && MathF.Abs(p.s.End.Z) < 1e-3f).Select(p => p.k).ToList();
        var firstBottom = floorMoves.First();
        var secondStart = floorMoves.First(k => path.Segments[k].End.X > 15f != path.Segments[firstBottom].End.X > 15f);
        var crossing = path.Segments.Skip(firstBottom).Take(secondStart - firstBottom).ToList();
        Assert.Contains(crossing, s => s.Kind == MoveKind.Rapid && MathF.Abs(s.End.Z - context.SafeZ) < 1e-3f);
        Assert.All(crossing.Where(s => s.Kind == MoveKind.Plunge), s => Assert.Equal(context.Parameters.FeedRate, s.FeedRate));
    }

    [Fact]
    public void EveryReachableCell_IsCutToItsFloor_AndNothingBelowIt()
    {
        var context = WallInStock(5f);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var stock = Simulate(context, path);
        var remaining = HeightMapDilation.ComputeRemaining(context.EffectiveTip, context.Profile);
        var tolerance = context.Parameters.Tolerance;
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                Assert.True(stock[i, j] <= remaining[i, j] + tolerance, $"rest material at ({i}, {j}): {stock[i, j]} above {remaining[i, j]}");
                Assert.True(stock[i, j] >= remaining[i, j] - tolerance, $"stock at ({i}, {j}) cut below what the floors allow: {stock[i, j]} under {remaining[i, j]}");
            }
        }
    }

    [Fact]
    public void Generate_IsDeterministic_AndReportsProgress()
    {
        var context = TestContexts.BumpPlate();
        var reports = new List<float>();
        var first = Strategy.Generate(context, new Progress(reports.Add), TestContext.Current.CancellationToken);
        var second = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Equal(first.Segments, second.Segments);
        Assert.NotEmpty(reports);
        Assert.Equal(1f, reports[^1], 4);
        Assert.True(reports.SequenceEqual(reports.OrderBy(r => r)), "progress went backwards");
    }

    [Fact]
    public void FlatModelAtTheStockTop_HasNoLevels_AndGivesAnEmptyProgram()
    {
        var context = TestContexts.Build(
            TestMeshes.Box(20, 20, 5),
            new StockDefinition { Shape = StockShape.Box, SizeX = 20, SizeY = 20, SizeZ = 5 },
            TestContexts.FlatTool6(),
            TestContexts.Parameters());
        Assert.Equal(0, context.Plan.Levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, path.Count);
    }

    [Fact]
    public void Generate_HonoursCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Strategy.Generate(TestContexts.BoxInStock(), null, source.Token));
    }

    private sealed class Progress : IProgress<StepProgress>
    {
        private readonly Action<float> _report;

        public Progress(Action<float> report) => _report = report;

        public void Report(StepProgress value) => _report(value.Fraction);
    }
}
