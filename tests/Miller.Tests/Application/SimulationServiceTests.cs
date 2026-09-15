using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

public sealed class SimulationServiceTests
{
    // 8 x 8 x 3 box on a 14 x 14 x 5 stock at 0.5 mm cells, cut with a 2 mm tool: the 3 mm ring around
    // the box is roughed in levels, so cutting starts within the first seconds.
    private static PipelineResult Result()
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Parameters.Stepover = 1;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);
    }

    private static SimulationService Loaded(PipelineResult? result = null)
    {
        var service = new SimulationService();
        service.Load(result ?? Result());
        return service;
    }

    [Fact]
    public void Load_CutsAClone_AndStartsPausedAtTheFirstSegment()
    {
        var result = Result();
        var service = Loaded(result);
        Assert.True(service.IsLoaded);
        Assert.False(service.IsPlaying);
        Assert.False(service.IsFinished);
        Assert.Equal(0f, service.Progress);
        Assert.NotSame(result.Stock.Map, service.Stock);
        Assert.Equal(result.Stock.Map.Z, service.Stock!.Z);
        Assert.Equal(result.Toolpath.Segments[0].Start, service.ToolPosition);
        Assert.Same(result, service.Result);
    }

    [Fact]
    public void PlayThenAdvance_CutsTheStock_StopRestoresIt()
    {
        var result = Result();
        var service = Loaded(result);
        var before = service.Stock;
        service.SpeedFactor = 10f;
        Assert.True(service.Advance(1).Dirty.IsEmpty, "paused: nothing moves");

        service.Play();
        Assert.True(service.IsPlaying);
        var snapshot = service.Advance(2);
        Assert.Equal(20, snapshot.ElapsedSimulated, 6);
        Assert.False(snapshot.Dirty.IsEmpty, $"nothing cut after 20 s at {snapshot.ToolPosition}, {snapshot.SegmentsCompleted} segments done");
        Assert.True(snapshot.Progress > 0f);
        Assert.Contains(service.Stock!.Z, z => z < result.Stock.StockTop);
        Assert.All(result.Stock.Map.Z, z => Assert.Equal(result.Stock.StockTop, z));
        Assert.Equal(snapshot.ToolPosition, service.ToolPosition);
        Assert.True(snapshot.SegmentsCompleted > 0);

        service.Stop();
        Assert.False(service.IsPlaying);
        Assert.NotSame(before, service.Stock);
        Assert.Equal(result.Stock.Map.Z, service.Stock!.Z);
        Assert.Equal(0f, service.Progress);
        Assert.Equal(0, service.ElapsedSimulated);
        Assert.Equal(result.Toolpath.Segments[0].Start, service.ToolPosition);
    }

    [Fact]
    public void RunToEnd_FinishesAndPauses_WithoutEventsForAGeneratedPath()
    {
        var service = Loaded();
        service.Play();
        var snapshot = service.RunToEnd();
        Assert.True(snapshot.Finished);
        Assert.True(service.IsFinished);
        Assert.False(service.IsPlaying);
        Assert.Equal(1f, service.Progress);
        Assert.Equal(service.Result!.Toolpath.Count, snapshot.SegmentsCompleted);
        Assert.Empty(service.Events);
        service.Play();
        Assert.False(service.IsPlaying, "a finished simulation does not play again until Stop");
    }

    [Fact]
    public void ManyAdvances_MatchRunToEnd_CellByCell()
    {
        var result = Result();
        var stepped = Loaded(result);
        stepped.SpeedFactor = 1000f;
        stepped.Play();
        var guard = 0;
        while (!stepped.IsFinished && guard++ < 100_000)
        {
            stepped.Advance(1.0 / 60);
        }

        Assert.True(stepped.IsFinished);
        var ran = Loaded(result);
        ran.RunToEnd();
        Assert.Equal(ran.Stock!.Z, stepped.Stock!.Z);
    }

    [Fact]
    public void Events_AccumulateOncePerSegmentAndKind_AndClearOnStop()
    {
        var result = Result();
        var top = result.Stock.StockTop;
        var bad = new Toolpath();
        // A rapid across the stock below its top (many samples inside), a rapid leaving the material
        // upwards (its start is inside), then a rapid above the stock.
        var rate = result.Toolpath.Segments[0].FeedRate;
        bad.Add(new ToolpathSegment(new Vector3(1, 6, top - 1), new Vector3(11, 6, top - 1), MoveKind.Rapid, rate));
        bad.Add(new ToolpathSegment(new Vector3(11, 6, top - 1), new Vector3(11, 6, top + 5), MoveKind.Rapid, rate));
        bad.Add(new ToolpathSegment(new Vector3(11, 6, top + 5), new Vector3(1, 6, top + 5), MoveKind.Rapid, rate));
        var service = Loaded(result with { Toolpath = bad });
        var snapshot = service.RunToEnd();
        Assert.Equal(2, service.Events.Count);
        Assert.All(service.Events, e => Assert.Equal(SimulationEventKind.RapidIntoMaterial, e.Kind));
        Assert.Equal(new[] { 0, 1 }, service.Events.Select(e => e.SegmentIndex));
        Assert.Equal(2, snapshot.NewEvents.Count);
        Assert.Empty(service.StepOnce(1).NewEvents);

        service.Stop();
        Assert.Empty(service.Events);
    }

    // T-103: a 1.5 mm cutter below a 4 mm head beside 3 mm walls; the strip the cutter leaves next
    // to the walls must keep the head clear, so the planned path produces no head event.
    [Fact]
    public void ShortCutterBesideWalls_ProducesNoHeadCollision()
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Tool.CutterLength = 1.5f;
        project.Parameters.Stepover = 1;
        project.Parameters.Stepdown = 1;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        var result = new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);
        Assert.Contains(result.HeadLimitedMask.Cast<bool>(), limited => limited);

        var service = new SimulationService();
        service.Load(result);
        service.RunToEnd();
        Assert.DoesNotContain(service.Events, e => e.Kind == SimulationEventKind.HeadCollision);
    }

    [Fact]
    public void SpeedFactor_IsForwardedAndClamped()
    {
        var service = new SimulationService();
        service.SpeedFactor = 5000f;
        Assert.Equal(SimulationClock.MaxSpeedFactor, service.SpeedFactor);
        service.SpeedFactor = 0.01f;
        Assert.Equal(SimulationClock.MinSpeedFactor, service.SpeedFactor);
        service.SpeedFactor = 2.5f;
        Assert.Equal(2.5f, service.SpeedFactor);
    }

    [Fact]
    public void WithoutALoadedResult_SimulationCallsThrow_AndUnloadClears()
    {
        var service = new SimulationService();
        Assert.False(service.IsLoaded);
        Assert.Throws<InvalidOperationException>(() => service.Advance(1));
        Assert.Throws<InvalidOperationException>(() => service.RunToEnd());
        Assert.Throws<InvalidOperationException>(() => service.Stop());
        service.Play();
        Assert.False(service.IsPlaying);

        service.Load(Result());
        service.Unload();
        Assert.False(service.IsLoaded);
        Assert.Null(service.Stock);
        Assert.Null(service.Result);
    }
}
