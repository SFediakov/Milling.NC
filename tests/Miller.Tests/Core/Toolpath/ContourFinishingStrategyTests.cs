using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class ContourFinishingStrategyTests
{
    [Fact]
    public void Registry_ListsTheStrategyAsFinishing()
    {
        var strategy = StrategyRegistry.GetById(ContourFinishingStrategy.StrategyId);
        Assert.IsType<ContourFinishingStrategy>(strategy);
        Assert.Equal(MillingOperation.Finishing, strategy.Operation);
    }

    [Fact]
    public void BumpPlate_LoopsPerLevelMatchMarchingSquaresAndNeverGouge()
    {
        var context = TestContexts.BumpPlate();
        var p = context.Parameters;
        var toolpath = new ContourFinishingStrategy().Generate(context, null, CancellationToken.None);
        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, p.Tolerance));

        var levels = Slicer.RoughingLevels(context.StockTop, context.Plan.LowestLevel, p.FinishingStepover).ToList();
        Assert.NotEmpty(levels);
        // Loops are linked in nearest-neighbour order and may be joined by short feeds, also across
        // consecutive levels; every level feed lies on a level and the sloping joins are few.
        var feeds = toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        var sloping = feeds.Where(s => s.Start.Z != s.End.Z).ToList();
        Assert.True(sloping.Count < levels.Count, $"{sloping.Count} sloping joins for {levels.Count} levels");
        Assert.All(feeds.Except(sloping), s => Assert.Contains(s.Start.Z, levels));

        foreach (var level in levels)
        {
            var expected = MarchingSquares.MaskContours(ContourFinishingStrategy.AllowedMask(context.EffectiveTip, level), context.EffectiveTip);
            var expectedPoints = expected.SelectMany(loop => loop).Select(v => (v.X, v.Y)).ToHashSet();
            var actualPoints = feeds.Where(s => s.Start.Z == level && s.End.Z == level)
                .SelectMany(s => new[] { (s.Start.X, s.Start.Y), (s.End.X, s.End.Y) }).ToHashSet();
            Assert.True(expectedPoints.SetEquals(actualPoints), $"level {level}: vertex sets differ");
            var plungesAtLevel = toolpath.Segments.Count(s => s.Kind == MoveKind.Plunge && s.End.Z == level);
            Assert.InRange(plungesAtLevel, 0, expected.Count);
        }
    }

    [Fact]
    public void Loops_AreClosed()
    {
        var context = TestContexts.BoxInStock();
        var toolpath = new ContourFinishingStrategy().Generate(context, null, CancellationToken.None);
        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, context.Parameters.Tolerance));

        // Each plunge starts a loop; the feed chain after it returns to the plunge end point before the
        // next retract (the linker may join the next loop to the chain when it starts within a cell).
        var segments = toolpath.Segments;
        for (var k = 0; k < segments.Count; k++)
        {
            if (segments[k].Kind != MoveKind.Plunge)
            {
                continue;
            }

            var start = segments[k].End;
            var n = k + 1;
            while (n < segments.Count && segments[n].Kind == MoveKind.Feed)
            {
                n++;
            }

            Assert.Contains(segments.Skip(k + 1).Take(n - k - 1), s => s.End == start);
            Assert.True(n - 1 - k >= 4, "a loop has at least four segments");
        }
    }

    [Fact]
    public void AllowedMask_FollowsTheTipMap()
    {
        var context = TestContexts.BoxInStock();
        // Rasterized heights carry float rounding (5.0000005), so the top level gets a small margin.
        var atTop = ContourFinishingStrategy.AllowedMask(context.EffectiveTip, context.StockTop + 1e-3f);
        var atFloor = ContourFinishingStrategy.AllowedMask(context.EffectiveTip, 0f);
        var top = atTop.Cast<bool>().Count(b => b);
        var floor = atFloor.Cast<bool>().Count(b => b);
        Assert.Equal(context.EffectiveTip.CellCount, top);
        Assert.True(floor > 0 && floor < top);
    }

    [Fact]
    public void Progress_AndCancellation()
    {
        var reports = new List<float>();
        new ContourFinishingStrategy().Generate(TestContexts.BumpPlate(), new SynchronousProgress(reports.Add), CancellationToken.None);
        Assert.Equal(1f, reports[^1]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ContourFinishingStrategy().Generate(TestContexts.BumpPlate(), null, cts.Token));
    }

    private sealed class SynchronousProgress : IProgress<float>
    {
        private readonly Action<float> _handler;

        public SynchronousProgress(Action<float> handler) => _handler = handler;

        public void Report(float value) => _handler(value);
    }
}
