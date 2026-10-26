using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// T-207: the simulation tab runs whole segments on the collision check's banded walk over the CPU
// workers in a session that keeps its inputs; Run to end, stepped playback, forward and backward seeks
// and layer jumps leave the stock bit for bit and find the events of the per-sample engine, which are
// the generation's own; a walk outside the toolpath is refused with a message.
public sealed class SimulationSessionTests
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

    // The slotted plate under a cutter shorter than the slot.
    private static PipelineResult SlottedPlate()
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
        project.Models.Add(new ModelPlacement { StlPath = "plate.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.SlottedPlate() }, null, CancellationToken.None);
    }

    // The slotted plate's result with its toolpath replaced by a zigzag 4 mm above the floor of the 12 mm
    // stock under the 6 mm cutter: the head enters the stock along every row. Since T-214 the generation
    // keeps no head event on its own fixtures (Backwards leaves those positions out), so the events come
    // from this path and the generation's check routine run on it (NativeCollisionCheck, mn_check_path).
    private static (PipelineResult Result, CollisionReport Check) DeepZigzag()
    {
        var plate = SlottedPlate();
        var path = new Toolpath();
        var at = new Vector3(1, 2, 18);
        path.Add(new ToolpathSegment(at, new Vector3(1, 2, 4), MoveKind.Plunge, plate.Parameters.FeedRate));
        at = new Vector3(1, 2, 4);
        for (var row = 0; row < 5; row++)
        {
            var across = new Vector3(row % 2 == 0 ? 19 : 1, at.Y, 4);
            path.Add(new ToolpathSegment(at, across, MoveKind.Feed, plate.Parameters.FeedRate));
            at = across;
            if (row < 4)
            {
                var up = new Vector3(at.X, at.Y + 4, 4);
                path.Add(new ToolpathSegment(at, up, MoveKind.Feed, plate.Parameters.FeedRate));
                at = up;
            }
        }

        path.Add(new ToolpathSegment(at, new Vector3(at.X, at.Y, 18), MoveKind.Rapid, plate.Parameters.RapidRate));
        var result = plate with { Toolpath = path, Layers = new[] { new ToolpathLayer(0, 4f) } };
        var check = NativeCollisionCheck.Run(path, plate.Stock.Map, plate.Model, plate.Floor, plate.Profile.Tool, CollisionDetector.Tolerance, out _);
        return (result, check);
    }

    private static (float[] Stock, List<(SimulationEventKind Kind, int Segment, Vector3 Tip, string Message)> Events) PerSample(PipelineResult result)
    {
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        var recorder = new CollisionRecorder(result.Profile, result.Profile.Tool.CutterLength, result.Model, result.Floor);
        var events = new List<(SimulationEventKind, int, Vector3, string)>();
        engine.Sampled += s =>
        {
            if (recorder.Record(stock, s) is { } found)
            {
                events.Add((found.Kind, found.SegmentIndex, found.Position, found.Message));
            }
        };
        engine.RunToEnd(TestContext.Current.CancellationToken);
        return (stock.Z, events);
    }

    private static (float[] Stock, List<(SimulationEventKind Kind, int Segment, Vector3 Tip, string Message)> Events) Session(PipelineResult result, Action<SimulationService> drive)
    {
        var service = new SimulationService();
        service.Load(result);
        try
        {
            drive(service);
            Assert.True(service.IsFinished);
            return (service.Stock!.Z, service.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position, e.Message)).ToList());
        }
        finally
        {
            service.Unload();
        }
    }

    private static void AssertSameAsPerSample(PipelineResult result) => AssertSameAsPerSample(result, result.Collisions.Report!);

    private static void AssertSameAsPerSample(PipelineResult result, CollisionReport check)
    {
        var reference = PerSample(result);
        var runs = new Dictionary<string, (float[] Stock, List<(SimulationEventKind, int, Vector3, string)> Events)>
        {
            ["run to end"] = Session(result, s => s.RunToEnd()),
            ["0.003 s steps"] = Session(result, s =>
            {
                while (!s.IsFinished)
                {
                    s.StepOnce(0.003);
                }
            }),
            ["0.7 s steps"] = Session(result, s =>
            {
                while (!s.IsFinished)
                {
                    s.StepOnce(0.7);
                }
            }),
            ["seeks"] = Session(result, s =>
            {
                for (var k = 1; k <= 37; k++)
                {
                    s.SeekTo(k / 37f * 0.5f);
                }

                s.SeekTo(0.8f);
                s.SeekTo(0.3f);
                s.SeekToSegment(result.Toolpath.Count / 2);
                s.SeekToPreviousLayer();
                s.SeekToNextLayer();
                s.SeekTo(0.65f);
                s.RunToEnd();
            }),
        };
        foreach (var (name, run) in runs)
        {
            Assert.True(reference.Stock.AsSpan().SequenceEqual(run.Stock), $"{name}: stock differs");
            Assert.Equal(reference.Events, run.Events);
        }

        var generation = check.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position, e.Message)).ToList();
        Assert.Equal(generation, reference.Events);
    }

    [Fact]
    public void Heart_TheSessionGivesThePerSampleStockAndEvents()
    {
        AssertSameAsPerSample(Heart(ZLayerByLayerStrategy.StrategyId, 0.4f));
    }

    [Fact]
    public void ACollidingPath_TheSessionGivesThePerSampleStockAndEvents()
    {
        var (result, check) = DeepZigzag();
        Assert.NotEmpty(check.Events);
        AssertSameAsPerSample(result, check);
    }

    [Fact]
    public void Walk_OutsideTheToolpath_IsRefusedWithAMessage()
    {
        var result = SlottedPlate();
        var stock = result.Stock.Map.Clone();
        using var session = new NativeSimulationSession(result.Toolpath, stock, result.Profile, CollisionDetector.Tolerance);
        Assert.Equal(result.Toolpath.Count, session.Count);
        var refused = Assert.Throws<ArgumentException>(() => session.Walk(stock, result.Model, result.Floor, 0, result.Toolpath.Count + 1, out _));
        Assert.Contains("segments 0 to", refused.Message, StringComparison.Ordinal);
        var empty = session.Walk(stock, result.Model, result.Floor, 3, 3, out var none);
        Assert.True(empty.IsEmpty);
        Assert.Empty(none);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Walk(stock, result.Model, result.Floor, 0, 1, out _));
    }

    // The heart at 0.2 mm cells: the session's Run to end against the per-sample engine, written to
    // out/simulation-timing.txt; both must give the same stock.
    [Fact]
    public void Heart_RunToEnd_IsMeasured_AndNotSlowerThanPerSample()
    {
        var result = Heart(ZLayerByLayerStrategy.StrategyId, 0.2f);
        var watch = Stopwatch.StartNew();
        var reference = PerSample(result);
        var perSample = watch.Elapsed.TotalSeconds;
        var service = new SimulationService();
        service.Load(result);
        watch.Restart();
        service.RunToEnd();
        var session = watch.Elapsed.TotalSeconds;
        Assert.True(reference.Stock.AsSpan().SequenceEqual(service.Stock!.Z));
        service.Unload();

        var outDir = Path.Combine(RepoRoot, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "simulation-timing.txt"), string.Create(CultureInfo.InvariantCulture,
            $"heart 0.2 mm, {result.Toolpath.Count} segments, {result.Stock.Map.CellCount} cells: per-sample {perSample:0.000} s, session {session:0.000} s ({Miller.Core.Native.NativeWorkers.Count} workers){Environment.NewLine}"));
        Assert.True(session <= perSample * 2, $"session {session:0.000} s against per-sample {perSample:0.000} s");
    }
}
