using System.Diagnostics;
using System.Numerics;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-144: the collision check cluster runs the generated toolpath once, records the collisions with the
// rule of the simulation panel, never touches the pipeline result and reports its own failures.
// Fixture: an 8 x 8 x 3 box in a 14 x 14 x 5 stock at 0.5 mm cells under a 2 mm cutter with a 4 mm head.
public sealed class CollisionServiceTests
{
    private static PipelineResult Box(float cutterLength)
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Tool.CutterLength = cutterLength;
        project.Parameters.Stepover = 1;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);
    }

    // World X of the first model cell in the middle row, the middle Y and the model top there.
    private static (float X, float Y, float Top) BoxEdge(PipelineResult result)
    {
        var model = result.Model;
        var j = model.Height / 2;
        for (var i = 0; i < model.Width; i++)
        {
            if (model[i, j] > result.Floor + 0.01f)
            {
                var center = model.CellCenter(i, j);
                return (center.X - model.CellSize / 2, center.Y, model[i, j]);
            }
        }

        throw new InvalidOperationException("The fixture has no model cell in its middle row.");
    }

    // A plunge beside the box whose head reaches over the part top, then a plunge in a stock corner
    // whose head only meets stock, each followed by a retract.
    private static Toolpath Colliding(PipelineResult result)
    {
        var (x0, y, top) = BoxEdge(result);
        var safe = result.SafeZ;
        var rate = result.Parameters.FeedRate;
        var besidePart = new Vector3(x0 - 1.5f, y, top - 2f);
        var origin = result.Stock.Map.CellCenter(3, 3);
        var corner = new Vector3(origin.X, origin.Y, result.Stock.StockTop - 2f);
        var path = new Toolpath();
        path.Add(new ToolpathSegment(besidePart with { Z = safe }, besidePart, MoveKind.Plunge, rate));
        path.Add(new ToolpathSegment(besidePart, besidePart with { Z = safe }, MoveKind.Rapid, rate));
        path.Add(new ToolpathSegment(besidePart with { Z = safe }, corner with { Z = safe }, MoveKind.Rapid, rate));
        path.Add(new ToolpathSegment(corner with { Z = safe }, corner, MoveKind.Plunge, rate));
        path.Add(new ToolpathSegment(corner, corner with { Z = safe }, MoveKind.Rapid, rate));
        return path;
    }

    [Fact]
    public void CollidingPath_GivesTheSimulationEvents_ModelAndStockContacts_AndLeavesTheResultIntact()
    {
        var result = Box(1f);
        result = result with { Toolpath = Colliding(result) };
        var stockBefore = (float[])result.Stock.Map.Z.Clone();

        var check = new CollisionService().Run(result, null, CancellationToken.None);
        Assert.True(check.Succeeded, check.Error);
        var report = check.Report!;

        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.NotEmpty(simulation.Events);
        Assert.Equal(simulation.Events, report.Events);

        Assert.True(report.Cells(CollisionContact.Model) > 0);
        Assert.True(report.Cells(CollisionContact.Stock) > 0);
        // The plunge beside the part and the start of its retract reach over the part top.
        Assert.Equal(2, report.ModelSegments);
        Assert.Equal(result.Model.CellCount, report.Contacts.Length);
        Assert.Equal(stockBefore, result.Stock.Map.Z);

        var summary = CollisionService.Summarize(check);
        Assert.Contains($"Collisions in {report.Segments} segments", summary);
        Assert.Contains("Touching the model: 2 segments", summary);
        Assert.Contains(report.Events[0].Message, summary);
        Assert.Equal($"collisions: {report.Events.Count}", CollisionService.StatusSuffix(check));
    }

    [Fact]
    public void PlannedPath_HasNoCollisions()
    {
        var check = new CollisionService().Run(Box(20f), null, CancellationToken.None);
        Assert.True(check.Succeeded);
        Assert.Empty(check.Report!.Events);
        Assert.All(check.Report.Contacts, c => Assert.Equal(CollisionContact.None, c));
        Assert.Equal(CollisionService.NoCollisionsText, CollisionService.Summarize(check));
        Assert.Equal("collisions: 0", CollisionService.StatusSuffix(check));
    }

    [Fact]
    public void Summary_ListsTheFirstEventsOnly()
    {
        var result = Box(20f);
        var top = result.Stock.StockTop;
        var path = new Toolpath();
        for (var k = 0; k < CollisionService.MaxListedEvents + 3; k++)
        {
            path.Add(new ToolpathSegment(new Vector3(1 + k, 1, top - 1), new Vector3(1 + k, 2, top - 1), MoveKind.Rapid, result.Parameters.FeedRate));
        }

        var check = new CollisionService().Run(result with { Toolpath = path }, null, CancellationToken.None);
        Assert.Equal(CollisionService.MaxListedEvents + 3, check.Report!.Events.Count);
        var summary = CollisionService.Summarize(check);
        Assert.Equal(CollisionService.MaxListedEvents, summary.Split('\n').Count(line => line.StartsWith("Rapid move in segment", StringComparison.Ordinal)));
        Assert.EndsWith("... and 3 more.", summary);
    }

    [Fact]
    public async Task Cancellation_EndsTheCheck()
    {
        var result = Box(20f);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => new CollisionService().Run(result, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollisionService().RunAsync(result, null, cancelled.Token));
    }

    // Insulation: a fault inside the cluster (here a profile on another grid) becomes a failed check.
    [Fact]
    public void FaultInsideTheCluster_IsAFailedCheck_NotAnException()
    {
        var result = Box(20f);
        var broken = result with { Profile = ToolProfile.Create(result.Profile.Tool, result.Profile.CellSize * 2) };
        var check = new CollisionService().Run(broken, null, CancellationToken.None);
        Assert.False(check.Succeeded);
        Assert.Null(check.Report);
        Assert.Contains("cell size", check.Error);
        Assert.StartsWith("The collision check failed: ", CollisionService.Summarize(check));
        Assert.Equal("collision check failed", CollisionService.StatusSuffix(check));
    }

    [Fact]
    public void Progress_RunsFromZeroToOne_Throttled()
    {
        var reports = new List<ProgressReport>();
        new CollisionService().Run(Box(20f), new SynchronousProgress(reports.Add), CancellationToken.None);
        Assert.All(reports, r => Assert.Equal(CollisionService.StageName, r.Stage));
        Assert.Equal(0f, reports[0].Fraction);
        Assert.Equal(1f, reports[^1].Fraction);
        Assert.Equal("collision check: 100%", reports[^1].Message);
        for (var k = 1; k < reports.Count; k++)
        {
            Assert.True(reports[k].Fraction - reports[k - 1].Fraction >= PipelineService.MinVisibleDelta || reports[k].Fraction == 1f);
        }

        Assert.True(reports.Count <= (int)(1f / PipelineService.MinVisibleDelta) + 2, $"{reports.Count} reports");
    }

    // The default heart job is free of collisions (T-136); the check time is written to the test output.
    [Fact]
    public void HeartFixture_HasNoCollisions()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 30;
        project.Stock.SizeY = 15;
        project.Stock.SizeZ = 25;
        project.Parameters.CellSize = 0.2f;
        project.Models.Add(new ModelPlacement { StlPath = TestMeshes.FixtureFileName });
        var import = new MeshImportService();
        import.Import(TestMeshes.FixturePath());
        var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var check = new CollisionService().Run(result, null, TestContext.Current.CancellationToken);
        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"collision check of the heart: {watch.ElapsedMilliseconds} ms over {result.Toolpath.Count} segments");
        Assert.True(check.Succeeded, check.Error);
        Assert.Empty(check.Report!.Events);
    }

    private sealed class SynchronousProgress : IProgress<ProgressReport>
    {
        private readonly Action<ProgressReport> _handler;

        public SynchronousProgress(Action<ProgressReport> handler) => _handler = handler;

        public void Report(ProgressReport value) => _handler(value);
    }
}
