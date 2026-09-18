using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// Free routes over the surface, one per level: Z follows the effective tip where a cell is finished, no
// move dips under it, every coverage cell ends at its floor, and the result is deterministic.
public sealed class ThreeAxisFreedomStrategyTests
{
    private static readonly ThreeAxisFreedomStrategy Strategy = new();

    private static HeightMap Simulate(ToolpathContext context, Toolpath path)
    {
        var stock = context.Stock.Clone();
        new SimulationEngine(path, stock, context.Profile).RunToEnd(TestContext.Current.CancellationToken);
        return stock;
    }

    [Fact]
    public void BumpPlate_FollowsTheSurface_WithoutLevelQuantization()
    {
        var context = TestContexts.BumpPlate();
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        var feeds = path.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.Contains(feeds, s => MathF.Abs(s.End.Z - s.Start.Z) > 0.1f && MathF.Abs(s.End.X - s.Start.X) + MathF.Abs(s.End.Y - s.Start.Y) > 0.1f);
        var heights = feeds.Select(s => s.End.Z).Distinct().Count();
        Assert.True(heights > 4 * context.Plan.Levels, $"only {heights} distinct feed heights for {context.Plan.Levels} levels");
        Assert.Equal(MoveKind.Plunge, path.Segments[0].Kind);
        Assert.Equal(MoveKind.Rapid, path.Segments[^1].Kind);
    }

    [Fact]
    public void EveryCoverageCell_IsCutToItsFloor_AndNothingBelowIt()
    {
        var context = TestContexts.BoxInStock();
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

    // The tip steps one cutter radius before the box wall (the reach floor beside the wall is the
    // box top), so the last floor cell before the step is at x = 1.75 for the 6 mm tool.
    [Fact]
    public void WallCells_AreNodes_SoTheWallIsCutAtTheOutline()
    {
        var context = TestContexts.BoxInStock();
        var map = context.EffectiveTip;
        var (wi, wj) = map.CellOf(1.75f, 10f);
        Assert.Equal(0f, map[wi, wj], 3);
        Assert.Equal(5f, map[wi + 1, wj], 3);
        Assert.True(ThreeAxisFreedomStrategy.IsStep(map, wi, wj, context.Parameters.Tolerance));
        var (fi, fj) = map.CellOf(1f, 1f);
        Assert.False(ThreeAxisFreedomStrategy.IsStep(map, fi, fj, context.Parameters.Tolerance));
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var center = map.CellCenter(wi, wj);
        Assert.Contains(path.Segments, s => s.Kind != MoveKind.Rapid && MathF.Abs(s.End.X - center.X) < 1e-3f && MathF.Abs(s.End.Y - center.Y) < 1e-3f && MathF.Abs(s.End.Z - map[wi, wj]) < 1e-3f);
    }

    [Fact]
    public void Generate_IsDeterministic_AndHonoursCancellation()
    {
        var context = TestContexts.BumpPlate();
        var first = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var second = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.Equal(first.Segments, second.Segments);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Strategy.Generate(context, null, source.Token));
    }

    [Fact]
    public void NoCoverage_GivesAnEmptyProgram()
    {
        var context = TestContexts.BoxInStock();
        var empty = new Miller.Core.Slicing.SlicePlan(context.Plan.Steps, new bool[context.Model.Width, context.Model.Height], context.Plan.LowestLevel);
        var path = Strategy.Generate(context with { Plan = empty }, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, path.Count);
        var parameters = new CuttingParameters { FeedRate = 0 };
        Assert.Throws<ArgumentException>(() => Strategy.Generate(context with { Parameters = parameters }, null, TestContext.Current.CancellationToken));
    }
}
