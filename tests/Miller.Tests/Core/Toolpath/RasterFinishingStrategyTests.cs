using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class RasterFinishingStrategyTests
{
    [Fact]
    public void Registry_ListsTheStrategyAsFinishing()
    {
        var strategy = StrategyRegistry.GetById(RasterFinishingStrategy.StrategyId);
        Assert.IsType<RasterFinishingStrategy>(strategy);
        Assert.Equal(MillingOperation.Finishing, strategy.Operation);
    }

    [Fact]
    public void BumpPlate_IsFinishedWithoutGougesAndEveryCellNearARow()
    {
        var parameters = TestContexts.Parameters();
        parameters.FinishingStepover = 1.5f;
        var context = TestContexts.BumpPlate(parameters: parameters);
        var toolpath = new RasterFinishingStrategy().Generate(context, null, CancellationToken.None);

        Assert.Empty(GougeChecker.Verify(toolpath, context.EffectiveTip, parameters.Tolerance));

        var map = context.EffectiveTip;
        var rowStep = RasterRows.RowStepCells(parameters.FinishingStepover, map.CellSize);
        var rowsUsed = toolpath.Segments
            .Where(s => s.Kind == MoveKind.Feed && s.Start.Y == s.End.Y)
            .Select(s => map.CellOf(s.Start.X, s.Start.Y).J)
            .ToHashSet();
        Assert.Equal(RasterRows.RowIndices(map.Height, rowStep).ToHashSet(), rowsUsed);
        for (var j = 0; j < map.Height; j++)
        {
            Assert.True(rowsUsed.Any(r => Math.Abs(r - j) <= rowStep), $"row {j} is farther than one stepover from every pass");
        }

        var feeds = toolpath.Segments.Count(s => s.Kind == MoveKind.Feed);
        Assert.True(feeds < map.MaterialCellCount(), $"{feeds} feed segments for {map.MaterialCellCount()} cells: merging failed");
        Assert.Equal(context.SafeZ, toolpath.Segments[0].Start.Z);
        Assert.Equal(MoveKind.Rapid, toolpath.Segments[^1].Kind);
    }

    [Fact]
    public void RowPoints_CrossBoundariesAtTheHigherTipAndMergeStraightStretches()
    {
        var map = new HeightMap(0, 0, 1f, 6, 1, 2f);
        map[3, 0] = 5f;
        var points = RasterFinishingStrategy.RowPoints(map, 0, 5, 0);
        Assert.Equal(new Vector3(0.5f, 0.5f, 2f), points[0]);
        Assert.Contains(new Vector3(3f, 0.5f, 5f), points);
        Assert.Contains(new Vector3(4f, 0.5f, 5f), points);
        Assert.Equal(new Vector3(5.5f, 0.5f, 2f), points[^1]);
        Assert.Equal(6, points.Count);

        var flat = RasterFinishingStrategy.RowPoints(new HeightMap(0, 0, 1f, 40, 1, 1f), 0, 39, 0);
        Assert.Equal(2, flat.Count);

        var reversed = RasterFinishingStrategy.RowPoints(map, 5, 0, 0);
        Assert.Equal(points[^1], reversed[0]);
        Assert.Equal(points[0], reversed[^1]);
    }

    [Fact]
    public void RowsWithNaN_AreSplitIntoRuns_AndAllNaNRowsAreSkipped()
    {
        var parameters = TestContexts.Parameters();
        parameters.FinishingStepover = 1f;
        var context = TestContexts.BumpPlate(parameters: parameters);
        var map = context.EffectiveTip.Clone();
        for (var j = 0; j < map.Height; j++)
        {
            map[10, j] = float.NaN;
        }

        for (var i = 0; i < map.Width; i++)
        {
            map[i, 4] = float.NaN;
        }

        var split = context with { EffectiveTip = map };
        var toolpath = new RasterFinishingStrategy().Generate(split, null, CancellationToken.None);
        Assert.Empty(GougeChecker.Verify(toolpath, map, parameters.Tolerance));
        var nanColumnX = map.CellCenter(10, 0).X;
        Assert.DoesNotContain(toolpath.Segments, s => s.Kind == MoveKind.Feed
            && (MathF.Abs(s.Start.X - nanColumnX) < 1e-4f || MathF.Abs(s.End.X - nanColumnX) < 1e-4f));
        var nanRowY = map.CellCenter(0, 4).Y;
        Assert.DoesNotContain(toolpath.Segments, s => s.Kind == MoveKind.Feed && s.Start.Y == nanRowY && s.End.Y == nanRowY);
    }

    [Fact]
    public void Progress_AndCancellation()
    {
        var reports = new List<float>();
        new RasterFinishingStrategy().Generate(TestContexts.BumpPlate(), new SynchronousProgress(reports.Add), CancellationToken.None);
        Assert.Equal(1f, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.OrderBy(r => r)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RasterFinishingStrategy().Generate(TestContexts.BumpPlate(), null, cts.Token));
    }

    private sealed class SynchronousProgress : IProgress<float>
    {
        private readonly Action<float> _handler;

        public SynchronousProgress(Action<float> handler) => _handler = handler;

        public void Report(float value) => _handler(value);
    }
}
