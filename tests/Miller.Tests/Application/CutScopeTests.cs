using Miller.Application.Services;
using Miller.Core.Analysis;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The separation scope through the whole pipeline: what is cut, what stands, and that the head
// clears the standing stock in the simulation.
public sealed class CutScopeTests
{
    private static MillingProject BoxProject(CutScope scope, float stockZ = 5f, float cutterLength = 20f)
    {
        var project = MillingProject.Default();
        project.CutScope = scope;
        project.Tool.CutterLength = cutterLength;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = stockZ;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project)
        => new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    [Fact]
    public void Separation_LeavesTheStockCornersAndFinishesTheModel()
    {
        var everything = Run(BoxProject(CutScope.Everything));
        var separation = Run(BoxProject(CutScope.Separation));
        Assert.Empty(GougeChecker.Verify(separation.Toolpath, separation.EffectiveTip, separation.Tolerance));
        Assert.True(separation.Statistics.FeedLength < everything.Statistics.FeedLength / 2,
            $"separation feed {separation.Statistics.FeedLength} is not far below {everything.Statistics.FeedLength}");
        Assert.All(separation.Plan.Steps.Zip(everything.Plan.Steps), pair => Assert.True(pair.First.MaskCount < pair.Second.MaskCount));

        var simulation = new SimulationService();
        simulation.Load(separation);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        var stock = simulation.Stock!;
        Assert.Equal(separation.Stock.StockTop, stock[0, 0], 3);
        Assert.Equal(separation.Stock.StockTop, stock[stock.Width - 1, stock.Height - 1], 3);
        Assert.Equal(separation.Stock.StockTop, separation.Standing[0, 0], 3);

        // The band beside the model wall is cut to the floor (the tool axis reaches the wall line by
        // majority, which also cuts the wall back and reports gouge), the model top is finished.
        var (bi, bj) = stock.CellOf(20f + 5f + 0.75f, 20f);
        Assert.Equal(separation.Floor, stock[bi, bj], 3);
        var analysis = FinalModelAnalyzer.Analyze(stock, separation.Model, separation.Floor, separation.Tolerance);
        Assert.True(analysis.GougeCells > 0);
        var (mi, mj) = stock.CellOf(20f, 20f);
        Assert.Equal(CellCategory.Ok, analysis.Map.Categories[stock.Index(mi, mj)]);

        // Everything scope: the corner is cut to the floor and nothing stands.
        var full = new SimulationService();
        full.Load(everything);
        full.RunToEnd();
        Assert.Equal(everything.Floor, full.Stock![0, 0], 3);
        Assert.All(everything.Standing.Z, z => Assert.True(float.IsNaN(z)));
    }

    [Fact]
    public void Separation_WithThreeAxisFreedom_LeavesTheCornersAndHasNoEvents()
    {
        var project = BoxProject(CutScope.Separation);
        project.RoutingStrategyId = "three-axis-freedom";
        var result = Run(project);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        var stock = simulation.Stock!;
        Assert.Equal(result.Stock.StockTop, stock[0, 0], 3);
        var (bi, bj) = stock.CellOf(20f + 5f + 0.75f, 20f);
        Assert.Equal(result.Floor, stock[bi, bj], 3);
        var (mi, mj) = stock.CellOf(20f, 20f);
        Assert.Equal(5f, stock[mi, mj], 2);
    }

    [Fact]
    public void Separation_MillsOutTheHoleIslandBelowTheVolume_EndToEnd()
    {
        var project = MillingProject.Default();
        project.CutScope = CutScope.Separation;
        project.Stock.SizeX = 50;
        project.Stock.SizeY = 50;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "ring.stl" });
        var mesh = Miller.Tests.Core.Slicing.MaterialIslandsTests.Ring();

        var kept = new PipelineService().Run(project, new[] { mesh }, null, CancellationToken.None);
        var stockKept = Simulate(kept);
        var (ci, cj) = stockKept.CellOf(25f, 25f);
        Assert.Equal(kept.Stock.StockTop, stockKept[ci, cj], 3);
        Assert.Equal(kept.Stock.StockTop, stockKept[0, 0], 3);

        project.MinIslandVolume = 4000f;
        var milled = new PipelineService().Run(project, new[] { mesh }, null, CancellationToken.None);
        Assert.Empty(GougeChecker.Verify(milled.Toolpath, milled.EffectiveTip, milled.Tolerance));
        var stockMilled = Simulate(milled);
        Assert.Equal(milled.Floor, stockMilled[ci, cj], 3);
        Assert.Equal(milled.Stock.StockTop, stockMilled[0, 0], 3);
        Assert.True(float.IsNaN(milled.Standing[ci, cj]));
        Assert.True(milled.Statistics.FeedLength > kept.Statistics.FeedLength);
        // The ring itself is finished the same way.
        var (ri, rj) = stockMilled.CellOf(10f, 25f);
        Assert.Equal(5f, stockMilled[ri, rj], 2);
    }

    private static Miller.Core.HeightMaps.HeightMap Simulate(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        return simulation.Stock!;
    }

    [Fact]
    public void Separation_NeverFeedsBelowTheStandingStock()
    {
        var result = Run(BoxProject(CutScope.Separation));
        var standing = result.Standing;
        foreach (var segment in result.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid))
        {
            var samples = Math.Max(1, (int)MathF.Ceiling(segment.Length / (standing.CellSize / 2)));
            for (var k = 0; k <= samples; k++)
            {
                var p = System.Numerics.Vector3.Lerp(segment.Start, segment.End, (float)k / samples);
                var (i, j) = standing.CellOf(p.X, p.Y);
                if (!standing.InBounds(i, j) || float.IsNaN(standing[i, j]))
                {
                    continue;
                }

                Assert.True(p.Z >= standing[i, j] - result.Tolerance, $"feed at {p} below standing {standing[i, j]}");
            }
        }
    }

    [Fact]
    public void DeepStock_ShortCutter_TerracedTrenchHasNoHeadCollision()
    {
        var project = BoxProject(CutScope.Separation, stockZ: 30f, cutterLength: 12f);
        var result = Run(project);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        var stock = simulation.Stock!;
        Assert.Equal(result.Stock.StockTop, stock[0, 0], 3);
        // The trench reaches the floor beyond the collar the head limit keeps beside the box.
        var (ti, tj) = stock.CellOf(20f + 5f + 5f + 0.75f, 20f);
        Assert.Equal(result.Floor, stock[ti, tj], 3);
        // Terrace: at half depth the trench is wider than at the floor.
        int TrenchCells(float z)
        {
            var count = 0;
            for (var i = stock.Width / 2; i < stock.Width; i++)
            {
                if (stock[i, tj] <= z + 1e-3f)
                {
                    count++;
                }
            }

            return count;
        }

        Assert.True(TrenchCells(15f) > TrenchCells(0f) + 4, $"{TrenchCells(15f)} cells at 15 vs {TrenchCells(0f)} at the floor");
    }

    [Fact]
    public void HeartFixture_SeparationHasNoCollisionsAndLessFeed()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 30;
        project.Stock.SizeY = 15;
        project.Stock.SizeZ = 25;
        project.Parameters.CellSize = 0.2f;
        project.Models.Add(new ModelPlacement { StlPath = TestMeshes.FixtureFileName });
        var import = new MeshImportService();
        import.Import(TestMeshes.FixturePath());

        var results = new Dictionary<CutScope, PipelineResult>();
        foreach (var scope in new[] { CutScope.Everything, CutScope.Separation })
        {
            project.CutScope = scope;
            var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
            Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
            var simulation = new SimulationService();
            simulation.Load(result);
            simulation.RunToEnd();
            Assert.Empty(simulation.Events);
            results[scope] = result;
        }

        Assert.True(results[CutScope.Separation].Statistics.FeedLength < results[CutScope.Everything].Statistics.FeedLength);
    }

    [Fact]
    public void Everything_IsTheDefault_AndRoundTripsThroughJson()
    {
        var project = MillingProject.Default();
        Assert.Equal(CutScope.Everything, project.CutScope);
        project.CutScope = CutScope.Separation;
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"CutScope\": \"Separation\"", json);
        Assert.Equal(CutScope.Separation, ProjectSerializer.Deserialize(json).CutScope);
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, ",\\s*\"CutScope\": \"Separation\"", string.Empty);
        Assert.DoesNotContain("CutScope", legacy);
        Assert.Equal(CutScope.Everything, ProjectSerializer.Deserialize(legacy).CutScope);
    }
}
