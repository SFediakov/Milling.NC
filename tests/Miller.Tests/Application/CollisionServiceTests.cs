using System.Diagnostics;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-147: the dynamic collision check runs inside the native generation and, on its own, through
// NativeCollisionCheck: the head ring and the rapid footprint against the stock as it stands, one
// event per segment and kind (the rule of the simulation panel), Model outranks Stock. The
// generation's own check closes every pass and its report reaches the summary text.
// Fixture: an 8 x 8 x 3 box in a 14 x 14 x 5 stock at 0.5 mm cells under a 2 mm cutter with a 4 mm head.
public sealed class CollisionServiceTests
{
    private static MillingProject BoxProject(float cutterLength)
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
        return project;
    }

    private static PipelineResult Box(float cutterLength)
        => new PipelineService().Run(BoxProject(cutterLength), new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);

    private static CollisionReport Check(PipelineResult result, Toolpath path, out CellStatus[] status)
        => NativeCollisionCheck.Run(path, result.Stock.Map, result.Model, result.Floor, result.Profile.Tool, CollisionDetector.Tolerance, out status);

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
    public void CollidingPath_GivesTheSimulationEvents_ModelAndStockContacts_AndLeavesTheStockIntact()
    {
        var result = Box(1f);
        var path = Colliding(result);
        var stockBefore = (float[])result.Stock.Map.Z.Clone();

        var report = Check(result, path, out var status);

        // The C# recorder over the same path finds the same events: every sample it takes is one the
        // native check takes as well.
        var simulation = new SimulationService();
        simulation.Load(result with { Toolpath = path });
        simulation.RunToEnd();
        Assert.NotEmpty(simulation.Events);
        Assert.Equal(simulation.Events.Select(e => (e.Kind, e.SegmentIndex)), report.Events.Select(e => (e.Kind, e.SegmentIndex)));
        // The native check samples the head along a segment as well, so its event names the first
        // colliding sample of the segment; the wording is the simulation panel's.
        Assert.All(report.Events, e => Assert.StartsWith(e.Kind == SimulationEventKind.HeadCollision ? "Head touches the stock in segment " : "Rapid move in segment ", e.Message));

        Assert.True(report.Cells(CollisionContact.Model) > 0);
        Assert.True(report.Cells(CollisionContact.Stock) > 0);
        // The plunge beside the part and the start of its retract reach over the part top.
        Assert.Equal(2, report.ModelSegments);
        Assert.Equal(result.Model.CellCount, report.Contacts.Length);
        Assert.Equal(stockBefore, result.Stock.Map.Z);

        // Status bits: Model where the model stands, Collision on every entered cell and nowhere else.
        for (var k = 0; k < status.Length; k++)
        {
            Assert.Equal(result.Model.Z[k] > result.Floor + 1e-4f, (status[k] & CellStatus.Model) != 0);
            Assert.Equal(report.Contacts[k] != CollisionContact.None, (status[k] & CellStatus.Collision) != 0);
            Assert.Equal(CellStatus.None, status[k] & (CellStatus.ShouldRemove | CellStatus.Forbidden));
        }

        var check = CollisionService.Of(report);
        var summary = CollisionService.Summarize(check);
        Assert.Contains($"Collisions in {report.Segments} segments", summary);
        Assert.Contains("Touching the model: 2 segments", summary);
        Assert.Contains(report.Events[0].Message, summary);
        Assert.Equal($"collisions: {report.Events.Count}", CollisionService.StatusSuffix(check));
    }

    // Order dependence: a cell already cut below the head underside is no collision, the same cell
    // uncut is one.
    [Fact]
    public void CellCutFirst_IsNoCollision_UncutItIs()
    {
        var result = Box(1f);
        var origin = result.Stock.Map.CellCenter(3, 3);
        var top = result.Stock.StockTop;
        var rate = result.Parameters.FeedRate;
        var deep = new Vector3(origin.X, origin.Y, top - 2f);
        var beside = new Vector3(origin.X + 2.5f, origin.Y, top - 2f);

        var uncut = new Toolpath();
        uncut.Add(new ToolpathSegment(deep with { Z = result.SafeZ }, deep, MoveKind.Plunge, rate));
        var report = Check(result, uncut, out _);
        Assert.Single(report.Events);
        Assert.Equal(SimulationEventKind.HeadCollision, report.Events[0].Kind);

        // The same plunge after a pass over the ring cells at head clearance: nothing stands in the way.
        var cutFirst = new Toolpath();
        var clearing = top - 1.5f;
        cutFirst.Add(new ToolpathSegment(beside with { Z = result.SafeZ }, beside with { Z = clearing }, MoveKind.Plunge, rate));
        cutFirst.Add(new ToolpathSegment(beside with { Z = clearing }, deep with { Z = clearing, X = deep.X - 2.5f }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing, X = deep.X - 2.5f }, deep with { Z = clearing, X = deep.X - 2.5f, Y = deep.Y + 2.5f }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing, X = deep.X - 2.5f, Y = deep.Y + 2.5f }, deep with { Z = clearing, Y = deep.Y + 2.5f, X = deep.X + 2.5f }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing, Y = deep.Y + 2.5f, X = deep.X + 2.5f }, deep with { Z = clearing, X = deep.X + 2.5f, Y = deep.Y - 2.5f }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing, X = deep.X + 2.5f, Y = deep.Y - 2.5f }, deep with { Z = clearing, X = deep.X - 2.5f, Y = deep.Y - 2.5f }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing, X = deep.X - 2.5f, Y = deep.Y - 2.5f }, deep with { Z = clearing }, MoveKind.Feed, rate));
        cutFirst.Add(new ToolpathSegment(deep with { Z = clearing }, deep, MoveKind.Plunge, rate));
        var after = Check(result, cutFirst, out _);
        Assert.DoesNotContain(after.Events, e => e.SegmentIndex == cutFirst.Count - 1);
    }

    [Fact]
    public void PlannedPath_HasNoCollisions()
    {
        var result = Box(20f);
        var check = result.Collisions;
        Assert.True(check.Succeeded);
        Assert.Empty(check.Report!.Events);
        Assert.All(check.Report.Contacts, c => Assert.Equal(CollisionContact.None, c));
        Assert.Equal(CollisionService.NoCollisionsText, CollisionService.Summarize(check));
        Assert.Equal("collisions: 0", CollisionService.StatusSuffix(check));
        Assert.Equal(1, result.Passes);
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

        var check = CollisionService.Of(Check(result, path, out _));
        Assert.Equal(CollisionService.MaxListedEvents + 3, check.Report!.Events.Count);
        var summary = CollisionService.Summarize(check);
        Assert.Equal(CollisionService.MaxListedEvents, summary.Split('\n').Count(line => line.StartsWith("Rapid move in segment", StringComparison.Ordinal)));
        Assert.EndsWith("... and 3 more.", summary);
    }

    // Insulation: bad arguments are refused before the native check runs.
    [Fact]
    public void GridMismatch_IsRefused()
    {
        var result = Box(20f);
        var other = new Miller.Core.HeightMaps.HeightMap(0, 0, 1, 3, 3, 0f);
        Assert.Throws<ArgumentException>(() => NativeCollisionCheck.Run(result.Toolpath, result.Stock.Map, other, result.Floor, result.Profile.Tool, CollisionDetector.Tolerance, out _));
        var badTool = new ToolDefinition { CutterDiameter = 2, HeadDiameter = 4, CutterLength = 0 };
        Assert.Throws<ArgumentException>(() => NativeCollisionCheck.Run(result.Toolpath, result.Stock.Map, result.Model, result.Floor, badTool, CollisionDetector.Tolerance, out _));
    }

    // A failed check is still summarised as such (the record keeps the error form for the window).
    [Fact]
    public void FailedCheck_SummarisesTheError()
    {
        var check = new CollisionCheck(null, "cell size differs");
        Assert.False(check.Succeeded);
        Assert.StartsWith("The collision check failed: ", CollisionService.Summarize(check));
        Assert.Equal("collision check failed", CollisionService.StatusSuffix(check));
    }

    // The default heart job stays free of collisions; the native check time is written to the test output.
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
        var report = Check(result, result.Toolpath, out _);
        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"collision check of the heart: {watch.ElapsedMilliseconds} ms over {result.Toolpath.Count} segments, {result.Passes} generation passes");
        Assert.Empty(report.Events);
        Assert.Empty(result.Collisions.Report!.Events);

        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
    }
}
