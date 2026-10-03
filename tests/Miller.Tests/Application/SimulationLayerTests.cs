using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The layers of a generated toolpath (one per run of consecutive routes at a plan level) and the
// layer by layer stepping of the simulation over them.
public sealed class SimulationLayerTests
{
    // 8 x 8 x 3 box on a 14 x 14 x 5 stock at 0.5 mm cells with a 2 mm tool: levels 3, 1 and 0, each
    // with a route in both strategies.
    private static MillingProject Project(string strategy)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Parameters.Stepover = 1;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Result(string strategy = ZLayerByLayerStrategy.StrategyId)
        => new PipelineService().Run(Project(strategy), new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);

    private static SimulationService Loaded(PipelineResult result)
    {
        var service = new SimulationService();
        service.Load(result);
        return service;
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void Layers_StartAtZero_IncreaseAndChangeLevel_AtRealSegmentStarts(string strategy)
    {
        var result = Result(strategy);
        var layers = result.Layers;
        var levels = result.Plan.Steps.Select(s => s.Level).ToList();
        Assert.NotEmpty(layers);
        Assert.Equal(0, layers[0].FirstSegment);
        Assert.All(layers, l => Assert.Contains(l.Level, levels));
        Assert.All(layers, l => Assert.True(l.FirstSegment < result.Toolpath.Count, $"layer at {l.FirstSegment} beyond {result.Toolpath.Count} segments"));
        for (var k = 1; k < layers.Count; k++)
        {
            Assert.True(layers[k].FirstSegment > layers[k - 1].FirstSegment, $"layer {k} starts at {layers[k].FirstSegment} after {layers[k - 1].FirstSegment}");
            Assert.NotEqual(layers[k - 1].Level, layers[k].Level);
            Assert.Equal(result.Toolpath.Segments[layers[k].FirstSegment - 1].End, result.Toolpath.Segments[layers[k].FirstSegment].Start);
        }
    }

    [Fact]
    public void ThreeAxisFreedom_HasOneLayerPerLevel_Descending()
    {
        var result = Result(ThreeAxisFreedomStrategy.StrategyId);
        var levels = result.Plan.Steps.Select(s => s.Level).ToList();
        Assert.Equal(levels, result.Layers.Select(l => l.Level).ToList());
    }

    [Fact]
    public void NextLayer_LandsOnTheNextLayerStart_WithTheStockOfAFreshSeek()
    {
        var result = Result();
        var service = Loaded(result);
        var layers = result.Layers;
        Assert.True(layers.Count >= 2, $"{layers.Count} layers");
        Assert.Equal(layers.Count, service.LayerCount);
        Assert.Equal(0, service.LayerIndex);
        Assert.Equal(0, service.CompletedLayers);

        var snapshot = service.SeekToNextLayer();
        Assert.Equal(layers[1].FirstSegment, snapshot.SegmentsCompleted);
        Assert.Equal(1, service.LayerIndex);
        Assert.Equal(1, service.CompletedLayers);
        Assert.False(snapshot.Finished);
        Assert.False(service.IsPlaying);
        Assert.True(service.Progress > 0f);
        Assert.Equal(result.Toolpath.Segments[layers[1].FirstSegment].Start, service.ToolPosition);
        var fresh = result.Stock.Map.Clone();
        new SimulationEngine(result.Toolpath, fresh, result.Profile).SeekToSegment(layers[1].FirstSegment);
        Assert.Equal(fresh.Z, service.Stock!.Z);

        // Back from a layer start: the start of the previous layer, on a fresh clone.
        var before = service.Stock;
        var back = service.SeekToPreviousLayer();
        Assert.Equal(0, back.SegmentsCompleted);
        Assert.Equal(0f, service.Progress);
        Assert.Equal(0, service.ElapsedSimulated);
        Assert.NotSame(before, service.Stock);
        Assert.Equal(result.Stock.Map.Z, service.Stock!.Z);
        Assert.Equal(0, service.LayerIndex);

        // Back from inside a layer: the start of that layer.
        service.SeekToNextLayer();
        service.StepOnce(0.2);
        Assert.Equal(1, service.LayerIndex);
        var inside = service.SeekToPreviousLayer();
        Assert.Equal(layers[1].FirstSegment, inside.SegmentsCompleted);
        Assert.Equal(1, service.LayerIndex);
        Assert.Equal(1, service.CompletedLayers);
    }

    [Fact]
    public void NextLayer_ThroughEveryLayer_FinishesOnTheLast_AndPreviousReturnsToItsStart()
    {
        var result = Result();
        var service = Loaded(result);
        var layers = result.Layers;
        for (var k = 0; k < layers.Count; k++)
        {
            Assert.Equal(k, service.CompletedLayers);
            var snapshot = service.SeekToNextLayer();
            var expected = k + 1 < layers.Count ? layers[k + 1].FirstSegment : result.Toolpath.Count;
            Assert.Equal(expected, snapshot.SegmentsCompleted);
            Assert.Equal(Math.Min(k + 1, layers.Count - 1), service.LayerIndex);
        }

        Assert.True(service.IsFinished);
        Assert.Equal(1f, service.Progress);
        Assert.Equal(layers.Count, service.CompletedLayers);
        Assert.False(service.IsPlaying);
        var whole = Loaded(result);
        whole.RunToEnd();
        Assert.Equal(whole.Stock!.Z, service.Stock!.Z);
        Assert.True(whole.ElapsedSimulated > 0, "run to end sets the clock to the total");
        Assert.Equal(whole.ElapsedSimulated, service.ElapsedSimulated, 6);
        Assert.Equal(result.Statistics.EstimatedMinutes * 60, service.ElapsedSimulated, 1);
        Assert.Equal(whole.Events.Count, service.Events.Count);

        var back = service.SeekToPreviousLayer();
        Assert.Equal(layers[^1].FirstSegment, back.SegmentsCompleted);
        Assert.False(service.IsFinished);
        Assert.Equal(layers.Count - 1, service.CompletedLayers);
    }

    [Fact]
    public void SeekToSegment_ForwardSweepsInPlace_BackwardReplays()
    {
        var result = Result();
        var service = Loaded(result);
        var middle = result.Toolpath.Count / 2;
        var stock = service.Stock;
        Assert.Equal(middle, service.SeekToSegment(middle).SegmentsCompleted);
        Assert.Same(stock, service.Stock);
        Assert.Equal(middle + 1, service.SeekToSegment(middle + 1).SegmentsCompleted);
        Assert.Same(stock, service.Stock);
        Assert.Equal(middle, service.SeekToSegment(middle).SegmentsCompleted);
        Assert.NotSame(stock, service.Stock);
        Assert.Equal(result.Toolpath.Count, service.SeekToSegment(int.MaxValue).SegmentsCompleted);
        Assert.True(service.IsFinished);
        Assert.Equal(0, service.SeekToSegment(-5).SegmentsCompleted);
        Assert.Equal(result.Stock.Map.Z, service.Stock!.Z);
    }
}
