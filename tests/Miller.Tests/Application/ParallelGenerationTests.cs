using System.Diagnostics;
using System.Globalization;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Simulation;
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
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CutScope.Everything)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CutScope.Separation)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, CutScope.Everything)]
    public void Box_OneWorkerAndAllWorkers_GiveTheSameGeneration(string strategy, CutScope scope)
    {
        AssertIdentical(Box(strategy, scope, 1), Box(strategy, scope, 0));
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void Heart_OneWorkerAndAllWorkers_GiveTheSameGeneration(string strategy)
    {
        var (project, meshes) = Heart(0.4f, strategy);
        AssertIdentical(Run(project, meshes, 1, null), Run(project, meshes, 0, null));
    }

    // Stage times of the heart sample at its own cell size with one worker and with all of them,
    // written to out/generation-timing.txt; the parallel stages must not make the whole slower.
    [Fact]
    public void Heart_StageTimes_AreReported_AndParallelIsNotSlower()
    {
        var lines = new List<string>();
        var slower = new List<string>();
        foreach (var strategy in new[] { ZLayerByLayerStrategy.StrategyId, ThreeAxisFreedomStrategy.StrategyId })
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
