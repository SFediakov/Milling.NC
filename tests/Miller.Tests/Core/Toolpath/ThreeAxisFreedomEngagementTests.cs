using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// The one-stepdown rule of "3 axis freedom": no sample of the tool removes more than Stepdown from
// any cell, the surface is still reached everywhere, and the id migration keeps old files loading.
public sealed class ThreeAxisFreedomEngagementTests
{
    private static readonly ThreeAxisFreedomStrategy Strategy = new();

    // Sweeps every cutting segment on a stock clone and returns the largest height any single sweep
    // removed from one cell.
    private static float MaxEngagement(ToolpathContext context, Toolpath path)
    {
        var stock = context.Stock.Clone();
        var worst = 0f;
        foreach (var segment in path.Segments)
        {
            if (segment.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var before = stock.Clone();
            var dirty = MaterialRemover.Sweep(stock, context.Profile, segment.Start, segment.End);
            if (dirty.IsEmpty)
            {
                continue;
            }

            for (var j = dirty.J0; j <= dirty.J1; j++)
            {
                for (var i = dirty.I0; i <= dirty.I1; i++)
                {
                    var removed = before[i, j] - stock[i, j];
                    if (!float.IsNaN(removed) && removed > worst)
                    {
                        worst = removed;
                    }
                }
            }
        }

        return worst;
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(1.5f)]
    [InlineData(1f)]
    public void BoxInStock_NeverCutsDeeperThanOneStepdown(float stepdown)
    {
        var parameters = TestContexts.Parameters();
        parameters.Stepdown = stepdown;
        var context = TestContexts.BoxInStock(parameters: parameters);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        Assert.True(path.Count > 0);
        var worst = MaxEngagement(context, path);
        Assert.True(worst <= stepdown + context.Parameters.Tolerance, $"a sweep removed {worst} mm with a stepdown of {stepdown}");
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
    }

    [Fact]
    public void BumpPlate_NeverCutsDeeperThanOneStepdown_AndFollowsTheSurface()
    {
        var context = TestContexts.BumpPlate();
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var worst = MaxEngagement(context, path);
        Assert.True(worst <= context.Parameters.Stepdown + context.Parameters.Tolerance, $"a sweep removed {worst} mm");
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
        // The last visit of a cell is at its own tip height: more distinct feed heights than levels.
        var heights = path.Segments.Where(s => s.Kind == MoveKind.Feed).Select(s => s.End.Z).Distinct().Count();
        Assert.True(heights > 4 * context.Plan.Levels, $"only {heights} distinct feed heights for {context.Plan.Levels} levels");
    }

    [Fact]
    public void EveryCoverageCell_StillEndsAtItsFloor()
    {
        var context = TestContexts.BoxInStock();
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var stock = context.Stock.Clone();
        new SimulationEngine(path, stock, context.Profile).RunToEnd(TestContext.Current.CancellationToken);
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
    public void PartWithinOneStepdown_IsOneRouteAtTheTip()
    {
        // 5 mm deep ring around the box with a 5 mm stepdown: one level, every node at its tip.
        var parameters = TestContexts.Parameters();
        parameters.Stepdown = 5f;
        var context = TestContexts.BoxInStock(parameters: parameters);
        Assert.Equal(1, context.Plan.Levels);
        var path = Strategy.Generate(context, null, TestContext.Current.CancellationToken);
        var plunges = path.Segments.Count(s => s.Kind == MoveKind.Plunge);
        Assert.True(plunges >= 1);
        Assert.Equal(MoveKind.Plunge, path.Segments[0].Kind);
        Assert.Equal(MoveKind.Rapid, path.Segments[^1].Kind);
        Assert.Empty(GougeChecker.Verify(path, context.EffectiveTip, context.Parameters.Tolerance));
    }

    [Fact]
    public void LevelMap_ClampsToTheLevel_AndKeepsNaN()
    {
        var tip = new HeightMap(0, 0, 1, 3, 1, 0f);
        tip[0, 0] = 5f;
        tip[1, 0] = float.NaN;
        tip[2, 0] = -2f;
        var level = ThreeAxisFreedomStrategy.LevelMap(tip, 1f);
        Assert.Equal(5f, level[0, 0]);
        Assert.True(float.IsNaN(level[1, 0]));
        Assert.Equal(1f, level[2, 0]);
        Assert.Equal(-2f, tip[2, 0]);
    }

    [Fact]
    public void Registry_AndLegacyId()
    {
        Assert.Equal(new[] { "z-layer-by-layer", "three-axis-freedom" }, StrategyRegistry.All.Select(s => s.Id));
        Assert.Equal("3 axis freedom", StrategyRegistry.GetById(ThreeAxisFreedomStrategy.StrategyId).DisplayName);
        var project = MillingProject.Default();
        project.RoutingStrategyId = ThreeAxisFreedomStrategy.LegacyStrategyId;
        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(ThreeAxisFreedomStrategy.StrategyId, loaded.RoutingStrategyId);
        Assert.DoesNotContain(ThreeAxisFreedomStrategy.LegacyStrategyId, ProjectSerializer.Serialize(loaded));
    }
}
