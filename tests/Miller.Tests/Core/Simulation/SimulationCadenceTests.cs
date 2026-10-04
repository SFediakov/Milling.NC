using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// T-161: the simulation's stock and collision events do not depend on how the playback splits the
// segments (frame time, speed factor, seeks), and the events are those of the generation's own
// collision check, which samples the same positions.
public sealed class SimulationCadenceTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static PipelineResult Heart(string strategy, float cellSize)
    {
        var samplePath = Path.Combine(RepoRoot, "samples", "heart.miller.json");
        var project = ProjectSerializer.Deserialize(File.ReadAllText(samplePath));
        project.Parameters.CellSize = cellSize;
        project.RoutingStrategyId = strategy;
        var import = new MeshImportService();
        foreach (var model in project.Models)
        {
            import.Import(Path.Combine(Path.GetDirectoryName(samplePath)!, model.StlPath));
        }

        return new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
    }

    // The short cutter under a frustum head with the far block (the fixture of the far stepdown tests).
    private static PipelineResult ShortCutterBox()
    {
        var project = MillingProject.Default();
        project.Tool.CutterLength = 2;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7;
        project.Tool.HeadTopDiameter = 20;
        project.Tool.HeadLength = 10;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.FarStepdown = 8;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 2) }, null, CancellationToken.None);
    }

    // The slotted plate under a cutter shorter than the slot with a zero ratio (CollisionModesTests):
    // the slot floor is finished and the head contacts with the plate top are kept as events.
    private static PipelineResult SlottedPlate(CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 3;
        project.Tool.HeadDiameter = 8;
        project.Tool.CutterLength = 6;
        project.Parameters.Stepover = 1.5f;
        project.Parameters.Stepdown = 2f;
        project.Stock.SizeX = TestMeshes.SlottedPlateSize;
        project.Stock.SizeY = TestMeshes.SlottedPlateSize;
        project.Stock.SizeZ = TestMeshes.SlottedPlateHeight;
        project.Parameters.CellSize = 0.5f;
        project.CollisionMode = mode;
        project.RecursionRatio = 0;
        project.OneRunRatio = 0;
        project.Models.Add(new ModelPlacement { StlPath = "plate.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.SlottedPlate() }, null, CancellationToken.None);
    }

    private static (float[] Stock, List<(SimulationEventKind Kind, int Segment, Vector3 Tip)> Events) Run(PipelineResult result, Action<SimulationEngine> drive)
    {
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        var recorder = new CollisionRecorder(result.Profile, result.Profile.Tool.CutterLength, result.Model, result.Floor);
        var events = new List<(SimulationEventKind, int, Vector3)>();
        engine.Sampled += s =>
        {
            if (recorder.Record(stock, s) is { } found)
            {
                events.Add((found.Kind, found.SegmentIndex, found.Position));
            }
        };
        drive(engine);
        Assert.True(engine.IsFinished);
        return (stock.Z, events);
    }

    private static void AssertCadenceFree(PipelineResult result)
    {
        var runToEnd = Run(result, e => e.RunToEnd(TestContext.Current.CancellationToken));
        var fine = Run(result, e =>
        {
            while (!e.IsFinished)
            {
                e.Step(0.003);
            }
        });
        var coarse = Run(result, e =>
        {
            while (!e.IsFinished)
            {
                e.Step(0.7);
            }
        });
        var seeks = Run(result, e =>
        {
            var parts = 37;
            for (var k = 1; k <= parts; k++)
            {
                e.SeekTo(e.TotalLength * k / parts);
            }

            e.RunToEnd(TestContext.Current.CancellationToken);
        });

        Assert.Equal(runToEnd.Stock, fine.Stock);
        Assert.Equal(runToEnd.Stock, coarse.Stock);
        Assert.Equal(runToEnd.Stock, seeks.Stock);
        Assert.Equal(runToEnd.Events, fine.Events);
        Assert.Equal(runToEnd.Events, coarse.Events);
        Assert.Equal(runToEnd.Events, seeks.Events);

        var generation = result.Collisions.Report!.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)).ToList();
        Assert.Equal(generation, runToEnd.Events);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void Heart_StockAndEvents_AreTheSameAtEveryCadence_AndEqualTheGenerationCheck(string strategy)
    {
        AssertCadenceFree(Heart(strategy, 0.4f));
    }

    [Fact]
    public void ShortCutterBox_WithTheFarBlock_IsCadenceFree_AndEqualsTheGenerationCheck()
    {
        AssertCadenceFree(ShortCutterBox());
    }

    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void SlottedPlate_WithHeadEvents_IsCadenceFree_AndEqualsTheGenerationCheck(CollisionMode mode)
    {
        var result = SlottedPlate(mode);
        Assert.NotEmpty(result.Collisions.Report!.Events);
        AssertCadenceFree(result);
    }

    [Fact]
    public void Rapids_AreSampledFromTheSegmentStart_WhateverThePartEnds()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0.25f, 0.25f, 2), new Vector3(10.25f, 0.25f, 2), MoveKind.Rapid, 3000));
        var profile = Miller.Core.HeightMaps.ToolProfile.Create(new ToolDefinition { CutterDiameter = 2, CutterLength = 10, HeadDiameter = 4 }, 0.5f);
        var whole = new List<Vector3>();
        var engine = new SimulationEngine(path, new Miller.Core.HeightMaps.HeightMap(0, 0, 0.5f, 24, 24, 5f), profile);
        engine.Sampled += s => whole.Add(s.Tip);
        engine.RunToEnd(TestContext.Current.CancellationToken);

        var parts = new List<Vector3>();
        var stepped = new SimulationEngine(path, new Miller.Core.HeightMaps.HeightMap(0, 0, 0.5f, 24, 24, 5f), profile);
        stepped.Sampled += s => parts.Add(s.Tip);
        while (!stepped.IsFinished)
        {
            stepped.Step(0.0013);
        }

        Assert.Equal(11, whole.Count);
        Assert.Equal(whole, parts);
    }
}
