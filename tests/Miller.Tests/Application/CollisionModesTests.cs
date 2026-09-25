using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-148 and T-150: the first pass runs without any head consideration; recursion resolves the
// dynamic check into should-remove stock and forbidden positions (X rule) and repeats while the
// collisions decrease; one run evaluates every node of every route before it is solved (Y rule).
// The fixture is an 8 x 8 x 3 box at the top of a 14 x 14 x 5 stock under a 2 mm cutter with a 4 mm
// head and a 2.5 mm cutter length: the head meets the box top from the floor around it.
public sealed class CollisionModesTests
{
    private static MillingProject BoxProject(string strategy, CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Tool.CutterLength = 2.5f;
        project.Parameters.Stepover = 1;
        project.Parameters.Stepdown = 1.5f;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Stock.AlignZ = StockAlignment.Max;
        project.Parameters.CellSize = 0.5f;
        project.RoutingStrategyId = strategy;
        project.CollisionMode = mode;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project)
        => new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);

    private static int Count(PipelineResult result, CellStatus bit) => result.Status.Count(s => (s & bit) != 0);

    private static IReadOnlyList<SimulationEvent> Simulated(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        return simulation.Events;
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Recursion)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, CollisionMode.Recursion)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.OneRun)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, CollisionMode.OneRun)]
    public void BoxBesideWall_EndsWithoutCollisions_InTheCheckAndInTheSimulation(string strategy, CollisionMode mode)
    {
        var result = Run(BoxProject(strategy, mode));
        Assert.True(result.Collisions.Succeeded);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Empty(Simulated(result));
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        Assert.Equal(0, Count(result, CellStatus.Collision));
        Assert.Equal(result.Passes, result.PassCollisions.Count);
        Assert.Equal(0, result.PassCollisions[^1].Events);
        if (mode == CollisionMode.OneRun)
        {
            Assert.Equal(1, result.Passes);
        }
        else
        {
            // The first pass has no head consideration, so the head meets the box top from the floor;
            // the resolution forbids those positions and the count falls to zero.
            Assert.True(result.Passes >= 2 && result.Passes <= PipelineService.MaxPasses, $"{result.Passes} passes");
            Assert.True(result.PassCollisions[0].Events > 0);
            Assert.True(result.PassCollisions[0].EnteredCells > result.PassCollisions[^1].EnteredCells);
            Assert.True(Count(result, CellStatus.Forbidden) > 0);
            Assert.True(result.HeadLimitedMask.Cast<bool>().Count(b => b) > 0);
        }
    }

    // Should-remove marks are stock cells only, and every one of them ends at or below the head
    // underside of the positions that met it (the simulation stock is below its closing plus the slack).
    [Fact]
    public void Recursion_ZLayer_MarksBlockingStockShouldRemove_AndCutsIt()
    {
        var result = Run(BoxProject(ZLayerByLayerStrategy.StrategyId, CollisionMode.Recursion));
        var shouldRemove = Count(result, CellStatus.ShouldRemove);
        Assert.True(shouldRemove > 0);
        Assert.Equal(shouldRemove, result.ShouldCut.Cast<bool>().Count(b => b));
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        var stock = simulation.Stock!;
        var closing = Miller.Core.HeightMaps.HeightMapDilation.ComputeRemaining(result.EffectiveTip, result.Profile);
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                var k = stock.Index(i, j);
                if ((result.Status[k] & CellStatus.ShouldRemove) == 0)
                {
                    continue;
                }

                Assert.Equal(CellStatus.None, result.Status[k] & CellStatus.Model);
                Assert.True(stock[i, j] <= closing[i, j] + result.Tolerance + CollisionDetector.Tolerance, $"({i},{j}) stands at {stock[i, j]}, closing {closing[i, j]}");
            }
        }
    }

    // The slotted plate under a cutter shorter than the slot: the head meets the plate top wherever
    // the cutter works on the slot floor. X and Y price a damaged model cell in finished model cells:
    // a large ratio forbids the slot (rest material, no collision), zero achieves it (the floor is
    // finished, the collisions are reported).
    private static MillingProject SlottedPlateProject(CollisionMode mode, float ratio)
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
        project.RecursionRatio = ratio;
        project.OneRunRatio = ratio;
        project.Models.Add(new ModelPlacement { StlPath = "plate.stl" });
        return project;
    }

    private static float SlotCenterStock(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        var (i, j) = simulation.Stock!.CellOf(TestMeshes.SlottedPlateSize / 2, TestMeshes.SlottedPlateSize / 2);
        return simulation.Stock[i, j];
    }

    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void LargeRatio_ForbidsTheSlot_ZeroRatioAchievesIt(CollisionMode mode)
    {
        var slotFloor = TestMeshes.SlottedPlateHeight - TestMeshes.SlotDepth;
        var strict = new PipelineService().Run(SlottedPlateProject(mode, 1e9f), new[] { TestMeshes.SlottedPlate() }, null, CancellationToken.None);
        Assert.Empty(strict.Collisions.Report!.Events);
        Assert.True(strict.HeadLimitedMask.Cast<bool>().Count(b => b) > 0);
        Assert.True(SlotCenterStock(strict) > slotFloor + 1f, "the slot floor was reached although every position there meets the plate top");

        var lenient = new PipelineService().Run(SlottedPlateProject(mode, 0f), new[] { TestMeshes.SlottedPlate() }, null, CancellationToken.None);
        Assert.NotEmpty(lenient.Collisions.Report!.Events);
        Assert.True(lenient.Collisions.Report.Cells(CollisionContact.Model) > 0);
        Assert.NotEmpty(Simulated(lenient));
        Assert.True(SlotCenterStock(lenient) <= slotFloor + lenient.Tolerance + CollisionDetector.Tolerance, "the slot floor was not finished");
        Assert.True(lenient.Statistics.FeedLength > strict.Statistics.FeedLength);
    }

    // A project without blocking material: one pass, no marks, and the head limit maps hold nothing.
    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void WithoutCollisions_OnePass_AndNoMarks(CollisionMode mode)
    {
        var project = BoxProject(ZLayerByLayerStrategy.StrategyId, mode);
        project.Tool.CutterLength = 20f;
        var result = Run(project);
        Assert.Equal(1, result.Passes);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Equal(0, Count(result, CellStatus.ShouldRemove | CellStatus.Forbidden | CellStatus.Collision));
        Assert.All(result.HeadLimit.Z, z => Assert.True(float.IsNaN(z)));
        Assert.DoesNotContain(result.HeadLimitedMask.Cast<bool>(), b => b);
        Assert.Equal(result.Tip.Z, result.EffectiveTip.Z);
    }

    // The heart in one run mode: zero collisions in one pass, as the recursion gives in two.
    [Fact]
    public void Heart_OneRun_HasNoCollisions()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 30;
        project.Stock.SizeY = 15;
        project.Stock.SizeZ = 25;
        project.Parameters.CellSize = 0.2f;
        project.CollisionMode = CollisionMode.OneRun;
        project.Models.Add(new ModelPlacement { StlPath = TestMeshes.FixtureFileName });
        var import = new MeshImportService();
        import.Import(TestMeshes.FixturePath());
        var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        Assert.Equal(1, result.Passes);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Empty(Simulated(result));
    }
}
