using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

public sealed class SimulationEngineTests
{
    private const float Cell = 0.5f;
    private const float Top = 5f;

    private static HeightMap Stock() => new(0, 0, Cell, 20, 20, Top);

    private static ToolProfile Profile(float diameter = 2f) => ToolProfile.Create(new ToolDefinition { CutterDiameter = diameter, CutterLength = 20, HeadDiameter = diameter + 4 }, Cell);

    // Rapid 1 mm (0.02 s), plunge 5 mm (1.5 s), feed 7 mm (0.525 s), rapid up 5 mm (0.1 s).
    private static Toolpath Path()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0.25f, 5.25f, 8), new Vector3(1.25f, 5.25f, 8), MoveKind.Rapid, 3000));
        path.Add(new ToolpathSegment(new Vector3(1.25f, 5.25f, 8), new Vector3(1.25f, 5.25f, 3), MoveKind.Plunge, 200));
        path.Add(new ToolpathSegment(new Vector3(1.25f, 5.25f, 3), new Vector3(8.25f, 5.25f, 3), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(8.25f, 5.25f, 3), new Vector3(8.25f, 5.25f, 8), MoveKind.Rapid, 3000));
        return path;
    }

    private const double TotalSeconds = 0.02 + 1.5 + 0.525 + 0.1;

    [Fact]
    public void OneFullStep_EqualsRunToEnd_CellByCell()
    {
        var stepped = Stock();
        var stepEngine = new SimulationEngine(Path(), stepped, Profile());
        var result = stepEngine.Step(TotalSeconds + 1);
        Assert.True(result.Finished);
        Assert.Equal(4, result.SegmentsCompleted);
        Assert.Equal(new Vector3(8.25f, 5.25f, 8), result.ToolPosition);

        var ran = Stock();
        var runEngine = new SimulationEngine(Path(), ran, Profile());
        runEngine.RunToEnd(TestContext.Current.CancellationToken);
        Assert.True(runEngine.IsFinished);
        Assert.Equal(stepped.Z, ran.Z);
        Assert.Contains(ran.Z, z => z == 3f);
        Assert.Equal(1f, runEngine.Progress);
    }

    [Fact]
    public void ManySmallSteps_EqualOneBigStep_AndProgressIsMonotonic()
    {
        var small = Stock();
        var engine = new SimulationEngine(Path(), small, Profile());
        var progress = 0f;
        var dirty = DirtyRect.Empty;
        for (var i = 0; i < 300; i++)
        {
            var r = engine.Step(0.01);
            dirty = dirty.Union(r.Dirty);
            Assert.True(engine.Progress >= progress, $"progress fell from {progress} to {engine.Progress} at step {i}");
            Assert.InRange(engine.Progress, 0f, 1f);
            progress = engine.Progress;
        }

        Assert.True(engine.IsFinished);
        var big = Stock();
        var bigDirty = new SimulationEngine(Path(), big, Profile()).Step(TotalSeconds + 1).Dirty;
        Assert.Equal(big.Z, small.Z);
        Assert.Equal(bigDirty, dirty);
        Assert.False(dirty.IsEmpty);
    }

    [Fact]
    public void Step_AdvancesAtTheSegmentRate()
    {
        var engine = new SimulationEngine(Path(), Stock(), Profile());
        var r = engine.Step(0.02 + 0.75);
        Assert.Equal(1, r.SegmentsCompleted);
        Assert.Equal(1, engine.CurrentSegmentIndex);
        Assert.Equal(new Vector3(1.25f, 5.25f, 5.5f), r.ToolPosition);
        Assert.False(r.Finished);
        Assert.Equal((1 + 2.5f) / 18f, engine.Progress, 4);
        Assert.True(r.Dirty.IsEmpty, "a plunge above the stock top removes nothing");
    }

    [Fact]
    public void Reset_RestoresStockToolPositionAndProgress()
    {
        var first = Stock();
        var engine = new SimulationEngine(Path(), first, Profile());
        engine.RunToEnd(TestContext.Current.CancellationToken);
        var fresh = Stock();
        engine.Reset(fresh);
        Assert.Same(fresh, engine.Stock);
        Assert.All(fresh.Z, z => Assert.Equal(Top, z));
        Assert.Equal(new Vector3(0.25f, 5.25f, 8), engine.ToolPosition);
        Assert.Equal(0f, engine.Progress);
        Assert.Equal(0, engine.CurrentSegmentIndex);
        Assert.False(engine.IsFinished);
        engine.RunToEnd(TestContext.Current.CancellationToken);
        Assert.Equal(first.Z, fresh.Z);
    }

    [Fact]
    public void Rapids_RemoveNothing_ButAreSampledAtCutterRadiusSpacing()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0.25f, 0.25f, 2), new Vector3(10.25f, 0.25f, 2), MoveKind.Rapid, 3000));
        var stock = Stock();
        var engine = new SimulationEngine(path, stock, Profile());
        var samples = new List<SimulationSample>();
        engine.Sampled += samples.Add;
        var r = engine.RunToEnd(TestContext.Current.CancellationToken);
        Assert.True(r.Dirty.IsEmpty);
        Assert.All(stock.Z, z => Assert.Equal(Top, z));
        Assert.Equal(1f, engine.RapidSampleSpacing);
        Assert.Equal(11, samples.Count);
        Assert.All(samples, s => Assert.Equal(MoveKind.Rapid, s.Kind));
        Assert.Equal(new Vector3(0.25f, 0.25f, 2), samples[0].Tip);
        Assert.Equal(new Vector3(10.25f, 0.25f, 2), samples[^1].Tip);
    }

    [Fact]
    public void EmptyToolpath_IsFinishedImmediately()
    {
        var engine = new SimulationEngine(new Toolpath(), Stock(), Profile());
        Assert.True(engine.IsFinished);
        Assert.Equal(1f, engine.Progress);
        var r = engine.Step(1);
        Assert.True(r.Finished);
        Assert.True(r.Dirty.IsEmpty);
    }

    [Fact]
    public void ProfileMustMatchTheStock()
    {
        Assert.Throws<ArgumentException>(() => new SimulationEngine(Path(), Stock(), ToolProfile.Create(new ToolDefinition(), 0.25f)));
    }
}
