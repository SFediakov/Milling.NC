using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.HeightMaps;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-160: the parallel stages of the generation give the same result with one worker and with all of
// them, bit for bit, in every map, segment, layer and collision; and the generation is not slower.
public sealed class ParallelGenerationTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static PipelineResult Box(string strategy, CutScope scope, int workers)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.CutScope = scope;
        project.Stock.SizeX = 30;
        project.Stock.SizeY = 30;
        project.Stock.SizeZ = 6;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.FarStepdown = strategy == ZLayerByLayerStrategy.StrategyId ? 4f : 0f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return Run(project, new[] { TestMeshes.Box(10, 10, 6) }, workers, null);
    }

    private static (MillingProject Project, IReadOnlyList<Miller.Core.Geometry.Mesh> Meshes) Heart(float cellSize, string strategy)
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

        return (project, import.Meshes);
    }

    private static PipelineResult Run(MillingProject project, IReadOnlyList<Miller.Core.Geometry.Mesh> meshes, int workers, IProgress<ProgressReport>? progress)
    {
        NativeWorkers.Limit(workers);
        try
        {
            return new PipelineService().Run(project, meshes, progress, CancellationToken.None);
        }
        finally
        {
            NativeWorkers.Limit(0);
        }
    }

    private static void AssertIdentical(PipelineResult one, PipelineResult all)
    {
        Assert.Equal(one.Model.Z, all.Model.Z);
        Assert.Equal(one.Tip.Z, all.Tip.Z);
        Assert.Equal(one.EffectiveTip.Z, all.EffectiveTip.Z);
        Assert.Equal(one.HeadLimit.Z, all.HeadLimit.Z);
        Assert.Equal(one.Standing.Z, all.Standing.Z);
        Assert.Equal(one.Plan.Steps.Select(s => s.Level), all.Plan.Steps.Select(s => s.Level));
        Assert.Equal(one.Toolpath.Segments, all.Toolpath.Segments);
        Assert.Equal(one.Layers, all.Layers);
        Assert.Equal(one.Statistics, all.Statistics);
        Assert.Equal(one.Status, all.Status);
        Assert.Equal(one.Passes, all.Passes);
        Assert.Equal(one.Collisions.Report!.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)), all.Collisions.Report!.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)));
    }

    // The frustum box of the Backwards and GPU tests: a head wider than the cutter over a box beside walls.
    private static PipelineResult Frustum(CollisionMode mode, string strategy, CutScope scope, float far, int workers)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.RoutingStrategyId = strategy;
        project.CutScope = scope;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterLength = 2f;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7f;
        project.Tool.HeadTopDiameter = 10f;
        project.Tool.HeadLength = 10f;
        project.Parameters.FarStepdown = far;
        project.Parameters.Stepover = 1f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return Run(project, new[] { TestMeshes.Box(10, 10, 4) }, workers, null);
    }

    // T-185: the slabs of Backwards are prepared and routed at once, the largest first; the toolpath, the
    // maps, the unreached and not-cut counts are the one worker's bit for bit.
    [Theory]
    [InlineData(CutScope.Everything, 0f)]
    [InlineData(CutScope.Everything, 4f)]
    [InlineData(CutScope.Separation, 4f)]
    public void Backwards_OneWorkerAndAllWorkers_GiveTheSameGeneration(CutScope scope, float far)
    {
        var one = Frustum(CollisionMode.Backwards, ZLayerByLayerStrategy.StrategyId, scope, far, 1);
        var all = Frustum(CollisionMode.Backwards, ZLayerByLayerStrategy.StrategyId, scope, far, 0);
        AssertIdentical(one, all);
        Assert.Equal(one.Unreached, all.Unreached);
        Assert.Equal(one.Uncut, all.Uncut);
    }

    // T-187: a check that finds hits (a zigzag fed 6 mm deep into a 9 mm stock under a 2 mm cutter of 2 mm
    // length, so the head enters the stock beside every row): the batches with a hit run as one walk, the
    // others in bands of rows, and the events and the cell status are the one walk's.
    [Fact]
    public void CheckWithHits_OneWorkerAndAllWorkers_GiveTheSameEvents()
    {
        var stock = new HeightMap(0, 0, 0.5f, 80, 80, 9f);
        var model = new HeightMap(0, 0, 0.5f, 80, 80, 0f);
        var tool = new ToolDefinition { CutterDiameter = 2f, CutterLength = 2f, HeadDiameter = 7f };
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(5, 5, 14), new Vector3(5, 5, 3), MoveKind.Plunge, 200f));
        var at = new Vector3(5, 5, 3);
        for (var row = 0; row < 7; row++)
        {
            var across = new Vector3(row % 2 == 0 ? 35 : 5, at.Y, 3);
            path.Add(new ToolpathSegment(at, across, MoveKind.Feed, 800f));
            at = across;
            if (row < 6)
            {
                var up = new Vector3(at.X, at.Y + 5, 3);
                path.Add(new ToolpathSegment(at, up, MoveKind.Feed, 800f));
                at = up;
            }
        }

        CollisionReport Check(int workers, out CellStatus[] status)
        {
            NativeWorkers.Limit(workers);
            try
            {
                return NativeCollisionCheck.Run(path, stock, model, 0f, tool, CollisionDetector.Tolerance, out status);
            }
            finally
            {
                NativeWorkers.Limit(0);
            }
        }

        var one = Check(1, out var oneStatus);
        var all = Check(0, out var allStatus);
        Assert.NotEmpty(one.Events);
        Assert.Equal(one.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)), all.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)));
        Assert.Equal(oneStatus, allStatus);
        Assert.Contains(oneStatus, s => (s & CellStatus.Collision) != 0);
    }

    [Fact]
    public void WorkerLimit_IsRespected()
    {
        var all = NativeWorkers.Count;
        Assert.True(all >= 1);
        NativeWorkers.Limit(1);
        try
        {
            Assert.Equal(1, NativeWorkers.Count);
        }
        finally
        {
            NativeWorkers.Limit(0);
        }

        Assert.Equal(all, NativeWorkers.Count);
        NativeWorkers.Limit(-3);
        Assert.Equal(all, NativeWorkers.Count);
        NativeWorkers.Limit(int.MaxValue);
        Assert.Equal(all, NativeWorkers.Count);
        NativeWorkers.Limit(0);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CutScope.Everything)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CutScope.Separation)]
    public void Box_OneWorkerAndAllWorkers_GiveTheSameGeneration(string strategy, CutScope scope)
    {
        AssertIdentical(Box(strategy, scope, 1), Box(strategy, scope, 0));
    }

    [Fact]
    public void Heart_OneWorkerAndAllWorkers_GiveTheSameGeneration()
    {
        var (project, meshes) = Heart(0.4f, ZLayerByLayerStrategy.StrategyId);
        AssertIdentical(Run(project, meshes, 1, null), Run(project, meshes, 0, null));
    }

    // Stage times of the heart sample at its own cell size with one worker and with all of them,
    // written to out/generation-timing.txt; the parallel stages must not make the whole slower.
    [Fact]
    public void Heart_StageTimes_AreReported_AndParallelIsNotSlower()
    {
        var lines = new List<string>();
        var slower = new List<string>();
        foreach (var strategy in new[] { ZLayerByLayerStrategy.StrategyId })
        {
            var (project, meshes) = Heart(0.2f, strategy);
            var totals = new Dictionary<int, double>();
            foreach (var workers in new[] { 1, 0 })
            {
                var stages = new Dictionary<string, double>();
                var order = new List<string>();
                var watch = Stopwatch.StartNew();
                var last = 0.0;
                var current = string.Empty;
                var progress = new SynchronousProgress(report =>
                {
                    var now = watch.Elapsed.TotalMilliseconds;
                    if (report.Stage != current)
                    {
                        if (current.Length > 0)
                        {
                            stages[current] = stages.GetValueOrDefault(current) + now - last;
                        }

                        if (!stages.ContainsKey(report.Stage))
                        {
                            order.Add(report.Stage);
                        }

                        current = report.Stage;
                        last = now;
                    }
                });
                Run(project, meshes, workers, progress);
                watch.Stop();
                stages[current] = stages.GetValueOrDefault(current) + watch.Elapsed.TotalMilliseconds - last;
                totals[workers] = watch.Elapsed.TotalMilliseconds;
                var label = workers == 1 ? "1 worker" : $"{NativeWorkers.Count} workers";
                var total = string.Create(CultureInfo.InvariantCulture, $"{strategy}, {label}: total {watch.Elapsed.TotalMilliseconds:0} ms; ");
                lines.Add(total + string.Join(", ", order.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s} {stages[s]:0} ms"))));
            }

            if (totals[0] > totals[1] * 1.2 + 200)
            {
                slower.Add(string.Create(CultureInfo.InvariantCulture, $"{strategy}: {totals[0]:0} ms with all workers against {totals[1]:0} ms with one"));
            }
        }

        Directory.CreateDirectory(Path.Combine(RepoRoot, "out"));
        File.WriteAllLines(Path.Combine(RepoRoot, "out", "generation-timing.txt"), lines);
        Assert.True(slower.Count == 0, string.Join("; ", slower));
    }

    private sealed class SynchronousProgress : IProgress<ProgressReport>
    {
        private readonly Action<ProgressReport> _handler;

        public SynchronousProgress(Action<ProgressReport> handler) => _handler = handler;

        public void Report(ProgressReport value) => _handler(value);
    }
}
