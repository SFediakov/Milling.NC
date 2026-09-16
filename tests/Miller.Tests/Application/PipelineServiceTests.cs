using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Io;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

public sealed class PipelineServiceTests
{
    private static MillingProject BoxProject()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 20;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static Mesh Box() => TestMeshes.Box(10, 10, 5);

    [Fact]
    public void MeshImportService_ImportsTheFixtureAndReportsIt()
    {
        var service = new MeshImportService();
        var raised = 0;
        service.MeshChanged += (_, _) => raised++;
        Assert.False(service.HasMesh);

        var report = service.Import(TestMeshes.FixturePath());
        Assert.True(service.HasMesh);
        Assert.Equal(1, raised);
        Assert.Equal(StlFormat.Binary, report.Format);
        Assert.Equal(4050, report.TriangleCount);
        Assert.Equal(21.971f, report.Bounds.Max.Z, 3);
        Assert.Same(report, Assert.Single(service.Reports));

        service.Clear();
        Assert.False(service.HasMesh);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void MeshImportService_RejectsAnEmptyMesh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"miller-empty-{Guid.NewGuid():N}.stl");
        File.WriteAllText(path, "solid empty\nendsolid empty\n");
        try
        {
            Assert.Throws<InvalidDataException>(() => new MeshImportService().Import(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoxProject_ProducesLevelsWithoutGouges()
    {
        var reports = new List<ProgressReport>();
        var result = await new PipelineService().RunAsync(BoxProject(), new[] { Box() }, new SynchronousProgress(reports.Add), CancellationToken.None);

        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Parameters.Tolerance));
        Assert.Equal(3, result.Plan.Levels);
        Assert.Equal(new[] { 3f, 1f, 0f }, result.Plan.Steps.Select(s => s.Level));
        Assert.Equal(5f, result.Stock.StockTop, 4);
        Assert.Equal(0f, result.Floor, 4);
        Assert.Equal(10f, result.SafeZ, 4);

        var feeds = result.Toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.Contains(feeds, s => s.Start.Z == 3f && s.End.Z == 3f);
        Assert.Contains(feeds, s => s.Start.Z == 0f && s.End.Z == 0f);
        Assert.Equal(result.Toolpath.Count, result.Statistics.SegmentCount);
        Assert.True(result.Statistics.EstimatedMinutes > 0);

        Assert.Equal("done", reports[^1].Stage);
        Assert.Equal(1f, reports[^1].Fraction);
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).OrderBy(f => f)), "progress went backwards");
        Assert.Contains(reports, r => r.Stage == "reach map");
        Assert.Contains(reports, r => r.Stage == "route");
        Assert.DoesNotContain(reports, r => r.Stage == "roughing" || r.Stage == "finishing");
    }

    [Fact]
    public async Task Fixture_RunsWithinTheBudgetWithoutGouges()
    {
        var project = MillingProject.Default();
        project.Parameters.CellSize = 0.2f;
        project.Models.Add(new ModelPlacement { StlPath = TestMeshes.FixtureFileName });
        var mesh = new MeshImportService();
        mesh.Import(TestMeshes.FixturePath());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new PipelineService().RunAsync(project, mesh.Meshes, null, CancellationToken.None);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"fixture pipeline took {watch.Elapsed}");

        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, project.Parameters.Tolerance));

        // The reach floor decides by majority, so beside the walls, where the footprint is mostly
        // stock, it lies below the model: the walls are cut back and the analysis reports gouge there.
        var below = 0;
        for (var k = 0; k < result.Model.CellCount; k++)
        {
            if (result.Model.Z[k] - result.EffectiveTip.Z[k] > project.Parameters.Tolerance)
            {
                below++;
            }
        }

        Assert.True(below > 0, "the heart walls are cut back where the footprint is mostly stock");

        var bounds = result.Toolpath.Bounds;
        Assert.True(bounds.Min.X >= result.Stock.Bounds.Min.X - 1e-3f && bounds.Max.X <= result.Stock.Bounds.Max.X + 1e-3f);
        Assert.True(bounds.Min.Y >= result.Stock.Bounds.Min.Y - 1e-3f && bounds.Max.Y <= result.Stock.Bounds.Max.Y + 1e-3f);
        Assert.True(bounds.Min.Z >= result.Floor - 1e-3f && bounds.Max.Z <= result.SafeZ + 1e-3f);
    }

    [Fact]
    public async Task Cancellation_BeforeCompletion_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PipelineService().RunAsync(BoxProject(), new[] { Box() }, null, cts.Token));
    }

    [Fact]
    public async Task InvalidProject_ThrowsValidationExceptionNamingTheField()
    {
        var project = BoxProject();
        project.Parameters.Stepdown = 0;
        var ex = await Assert.ThrowsAsync<ValidationException>(() => new PipelineService().RunAsync(project, new[] { Box() }, null, CancellationToken.None));
        Assert.Contains("Parameters.Stepdown", ex.Message);
        Assert.Contains(ex.Result.Errors, e => e.Field == "Parameters.Stepdown");
    }

    [Fact]
    public void UnknownStrategyId_Throws()
    {
        var unknown = BoxProject();
        unknown.RoutingStrategyId = "missing";
        Assert.Throws<KeyNotFoundException>(() => new PipelineService().Run(unknown, new[] { Box() }, null, CancellationToken.None));
    }

    private sealed class SynchronousProgress : IProgress<ProgressReport>
    {
        private readonly Action<ProgressReport> _handler;

        public SynchronousProgress(Action<ProgressReport> handler) => _handler = handler;

        public void Report(ProgressReport value) => _handler(value);
    }
}
