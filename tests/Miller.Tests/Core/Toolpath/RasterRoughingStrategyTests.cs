using System.Numerics;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class RasterRoughingStrategyTests
{
    private static readonly float[] Levels = { 3f, 1f, 0f };

    [Fact]
    public void Registry_ListsTheStrategyAsRoughing()
    {
        var strategy = StrategyRegistry.GetById(RasterRoughingStrategy.StrategyId);
        Assert.IsType<RasterRoughingStrategy>(strategy);
        Assert.Equal(MillingOperation.Roughing, strategy.Operation);
        Assert.Contains(strategy, StrategyRegistry.ForOperation(MillingOperation.Roughing));
    }

    [Fact]
    public void BoxInStock_ClearsTheRingWithoutGouging()
    {
        var context = TestContexts.BoxInStock();
        Assert.Equal(Levels, context.Plan.RoughingSteps.Select(s => s.Level));

        var toolpath = new RasterRoughingStrategy().Generate(context, null, CancellationToken.None);
        var feeds = toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.NotEmpty(feeds);
        Assert.All(feeds, s =>
        {
            Assert.False(InsideBox(s.Start), $"feed starts inside the box: {s.Start}");
            Assert.False(InsideBox(s.End), $"feed ends inside the box: {s.End}");
            Assert.Contains(s.Start.Z, Levels);
            Assert.Equal(s.Start.Z, s.End.Z);
        });

        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, context.Parameters.Tolerance));
        Assert.Equal(context.SafeZ, toolpath.Segments[0].Start.Z);
        Assert.Equal(MoveKind.Plunge, toolpath.Segments[0].Kind);
        Assert.Equal(MoveKind.Rapid, toolpath.Segments[^1].Kind);
        Assert.Equal(context.SafeZ, toolpath.Segments[^1].End.Z);

        // Every level cuts at least one full row across the ring below the box.
        foreach (var level in Levels)
        {
            Assert.Contains(feeds, s => s.Start.Z == level && s.Start.Y < 5 && MathF.Abs(s.End.X - s.Start.X) > 15);
        }
    }

    [Fact]
    public void Zigzag_JoinsRowsThroughMaskedCells_OneWayDoesNot()
    {
        var zigzag = new RasterRoughingStrategy().Generate(TestContexts.BoxInStock(), null, CancellationToken.None);
        var oneWayParameters = TestContexts.Parameters();
        oneWayParameters.Direction = MillingDirection.OneWay;
        var oneWayContext = TestContexts.BoxInStock(parameters: oneWayParameters);
        var oneWay = new RasterRoughingStrategy().Generate(oneWayContext, null, CancellationToken.None);

        var zigzagPlunges = zigzag.Segments.Count(s => s.Kind == MoveKind.Plunge);
        var oneWayPlunges = oneWay.Segments.Count(s => s.Kind == MoveKind.Plunge);
        Assert.True(zigzagPlunges < oneWayPlunges, $"zigzag {zigzagPlunges} plunges, one-way {oneWayPlunges}");

        var oneWayRows = oneWay.Segments.Where(s => s.Kind == MoveKind.Feed && s.Start.Y == s.End.Y && s.Start.X != s.End.X);
        Assert.All(oneWayRows, s => Assert.True(s.End.X > s.Start.X));
        Assert.Empty(GougeChecker.Verify(oneWay, oneWayContext.EffectiveTip, oneWayContext.Parameters.Tolerance));
    }

    [Fact]
    public void Progress_ReachesOne_AndCancellationThrows()
    {
        var reports = new List<float>();
        var progress = new SynchronousProgress(reports.Add);
        new RasterRoughingStrategy().Generate(TestContexts.BoxInStock(), progress, CancellationToken.None);
        Assert.Equal(Levels.Length, reports.Count);
        Assert.Equal(1f, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.OrderBy(r => r)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RasterRoughingStrategy().Generate(TestContexts.BoxInStock(), null, cts.Token));
    }

    [Fact]
    public void Helpers_ComputeRowsAndRuns()
    {
        Assert.Equal(6, RasterRows.RowStepCells(3f, 0.5f));
        Assert.Equal(1, RasterRows.RowStepCells(0.1f, 0.5f));
        Assert.Equal(new[] { 0, 6, 12, 18, 19 }, RasterRows.RowIndices(20, 6));
        Assert.Equal(new[] { 0, 6, 12, 18 }, RasterRows.RowIndices(19, 6));

        var mask = new bool[8, 1];
        foreach (var i in new[] { 1, 2, 3, 5, 7 })
        {
            mask[i, 0] = true;
        }

        Assert.Equal(new[] { (1, 3), (5, 5), (7, 7) }, RasterRows.Runs(i => mask[i, 0], 8, true));
        Assert.Equal(new[] { (7, 7), (5, 5), (3, 1) }, RasterRows.Runs(i => mask[i, 0], 8, false));
    }

    private static bool InsideBox(Vector3 p) => p.X > 5 && p.X < 15 && p.Y > 5 && p.Y < 15;

    private sealed class SynchronousProgress : IProgress<float>
    {
        private readonly Action<float> _handler;

        public SynchronousProgress(Action<float> handler) => _handler = handler;

        public void Report(float value) => _handler(value);
    }
}
