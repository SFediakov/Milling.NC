using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class LayerCompleteStrategyTests
{
    [Fact]
    public void Registry_ListsTheStrategyAsRoughing_AndItIsTheDefault()
    {
        var strategy = StrategyRegistry.GetById(LayerCompleteStrategy.StrategyId);
        Assert.IsType<LayerCompleteStrategy>(strategy);
        Assert.Equal(MillingOperation.Roughing, strategy.Operation);
        Assert.Equal(LayerCompleteStrategy.StrategyId, MillingProject.DefaultRoughingStrategyId);
    }

    [Fact]
    public void BoxInStock_EveryLevelIsFinishedBeforeTheNext_AndNothingGouges()
    {
        var context = TestContexts.BoxInStock();
        var toolpath = new LayerCompleteStrategy().Generate(context, null, CancellationToken.None);
        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, context.Parameters.Tolerance));

        var levels = context.Plan.RoughingSteps.Select(s => s.Level).ToList();
        var feedLevels = toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).Select(s => s.Start.Z).ToList();
        Assert.NotEmpty(feedLevels);
        Assert.All(feedLevels, z => Assert.Contains(z, levels));
        // Levels appear in plan order and never return to an earlier one.
        var order = feedLevels.Select(z => levels.IndexOf(z)).ToList();
        for (var k = 1; k < order.Count; k++)
        {
            Assert.True(order[k] >= order[k - 1], $"level {feedLevels[k]} follows level {feedLevels[k - 1]}");
        }

        Assert.Equal(levels.Count, order.Distinct().Count());
    }

    [Fact]
    public void BoxInStock_ContainsRowsAndAProfileLoopPerLevel()
    {
        var context = TestContexts.BoxInStock();
        var rows = new RasterRoughingStrategy().Generate(context, null, CancellationToken.None);
        var complete = new LayerCompleteStrategy().Generate(context, null, CancellationToken.None);
        Assert.True(complete.TotalLength(MoveKind.Feed) > rows.TotalLength(MoveKind.Feed), "the profile passes add feed length");

        foreach (var step in context.Plan.RoughingSteps)
        {
            var loops = ContourFinishingStrategy.LoopPasses(step.Mask, context.EffectiveTip, step.Level, context.Parameters);
            Assert.NotEmpty(loops);
            var loopFeed = loops.Sum(l => l.TotalLength(MoveKind.Feed));
            var rowFeed = RasterRoughingStrategy.RowPasses(context.EffectiveTip, step.Mask, step.Level, context.Parameters, CancellationToken.None).Sum(l => l.TotalLength(MoveKind.Feed));
            var atLevel = complete.Segments.Where(s => s.Kind == MoveKind.Feed && s.Start.Z == step.Level).Sum(s => s.Length);
            // Row feeds plus loop feeds, plus at most the zigzag joins the linker adds at the level.
            Assert.True(atLevel >= loopFeed + rowFeed - 1e-3f, $"level {step.Level}: {atLevel} < {loopFeed + rowFeed}");
        }
    }

    [Fact]
    public void BumpPlate_DoesNotGouge()
    {
        var context = TestContexts.BumpPlate();
        var toolpath = new LayerCompleteStrategy().Generate(context, null, CancellationToken.None);
        Assert.NotEmpty(toolpath.Segments);
        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, context.Parameters.Tolerance));
    }

    [Fact]
    public void Cancellation_StopsGeneration()
    {
        var context = TestContexts.BoxInStock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new LayerCompleteStrategy().Generate(context, null, cts.Token));
    }
}
