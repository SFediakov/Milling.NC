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
        Assert.Same(report, service.Report);

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
    public async Task BoxProject_ProducesRoughingAndFinishingWithoutGouges()
    {
        var reports = new List<ProgressReport>();
        var result = await new PipelineService().RunAsync(BoxProject(), Box(), new SynchronousProgress(reports.Add), CancellationToken.None);

        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Parameters.Tolerance));
        Assert.Equal(3, result.Plan.RoughingLevels);
        Assert.True(result.Plan.HasFinishing);
        Assert.Equal(5f, result.Stock.StockTop, 4);
        Assert.Equal(0f, result.Floor, 4);
        Assert.Equal(10f, result.SafeZ, 4);

        var feeds = result.Toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.Contains(feeds, s => s.Start.Z == 3f);
        // The rasterized box top carries float rounding (5.0000005), so the finishing height is compared with a tolerance.
        // Merged finishing rows span the whole box top (x from 2 to 18), so the check is by overlap.
        Assert.Contains(feeds, s => MathF.Abs(s.Start.Z - 5f) < 1e-3f && MathF.Min(s.Start.X, s.End.X) < 15 && MathF.Max(s.Start.X, s.End.X) > 5);
        Assert.Equal(result.Toolpath.Count, result.Statistics.SegmentCount);
        Assert.True(result.Statistics.EstimatedMinutes > 0);

        Assert.Equal("done", reports[^1].Stage);
        Assert.Equal(1f, reports[^1].Fraction);
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).OrderBy(f => f)), "progress went backwards");
        Assert.Contains(reports, r => r.Stage == "roughing");
        Assert.Contains(reports, r => r.Stage == "finishing");
    }

    [Fact]
    public async Task Cancellation_BeforeCompletion_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PipelineService().RunAsync(BoxProject(), Box(), null, cts.Token));
    }

    [Fact]
    public async Task InvalidProject_ThrowsValidationExceptionNamingTheField()
    {
        var project = BoxProject();
        project.Parameters.Stepdown = 0;
        var ex = await Assert.ThrowsAsync<ValidationException>(() => new PipelineService().RunAsync(project, Box(), null, CancellationToken.None));
        Assert.Contains("Parameters.Stepdown", ex.Message);
        Assert.Contains(ex.Result.Errors, e => e.Field == "Parameters.Stepdown");
    }

    [Fact]
    public void UnknownOrMismatchedStrategyIds_Throw()
    {
        var unknown = BoxProject();
        unknown.RoughingStrategyId = "missing";
        Assert.Throws<KeyNotFoundException>(() => new PipelineService().Run(unknown, Box(), null, CancellationToken.None));

        var mismatched = BoxProject();
        mismatched.FinishingStrategyId = "raster-roughing";
        Assert.Throws<ArgumentException>(() => new PipelineService().Run(mismatched, Box(), null, CancellationToken.None));
    }

    [Fact]
    public void Join_ConnectsTheTwoPathsWithOneRapidAtSafeZ()
    {
        var parameters = TestContexts.Parameters();
        var a = new Toolpath();
        a.Add(new ToolpathSegment(new System.Numerics.Vector3(0, 0, 10), new System.Numerics.Vector3(0, 0, 0), MoveKind.Plunge, 200));
        a.Add(new ToolpathSegment(new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Vector3(0, 0, 10), MoveKind.Rapid, 3000));
        var b = new Toolpath();
        b.Add(new ToolpathSegment(new System.Numerics.Vector3(5, 5, 10), new System.Numerics.Vector3(5, 5, 0), MoveKind.Plunge, 200));
        var joined = PipelineService.Join(a, b, parameters);
        Assert.Equal(4, joined.Count);
        Assert.Equal(MoveKind.Rapid, joined.Segments[2].Kind);
        Assert.Equal(new System.Numerics.Vector3(5, 5, 10), joined.Segments[2].End);
        Assert.Equal(2, PipelineService.Join(a, new Toolpath(), parameters).Count);
        Assert.Equal(1, PipelineService.Join(new Toolpath(), b, parameters).Count);
    }

    private sealed class SynchronousProgress : IProgress<ProgressReport>
    {
        private readonly Action<ProgressReport> _handler;

        public SynchronousProgress(Action<ProgressReport> handler) => _handler = handler;

        public void Report(ProgressReport value) => _handler(value);
    }
}
