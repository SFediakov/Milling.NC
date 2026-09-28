using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// A move over stock no route has cut keeps the safe height above the stock top. The fixture is a
// 10 mm wall flush with the top of a 40 x 20 x 6 stock and as long as the stock, so the two pockets
// beside it are only joined over the wall top. The route used to cross it as a feed exactly on the
// stock top (faster than the retract); now the crossing climbs to the safe plane and moves as a rapid.
public sealed class UncutStockTravelTests
{
    private static PipelineResult Run(string strategy)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 6;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "wall.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 20, 6) }, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void CrossingTheUncutWall_UsesTheSafePlane(string strategy)
    {
        var result = Run(strategy);
        var top = result.Stock.StockTop;
        var moving = result.Toolpath.Segments.Where(s => Vector2.Distance(new Vector2(s.Start.X, s.Start.Y), new Vector2(s.End.X, s.End.Y)) > 1e-3f).ToList();
        Assert.DoesNotContain(moving, s => s.Kind != MoveKind.Rapid && s.Start.Z >= top - 1e-3f && s.End.Z >= top - 1e-3f);
        // The wall spans x = 15 to 25.
        Assert.Contains(moving, s => s.Kind == MoveKind.Rapid && MathF.Min(s.Start.X, s.End.X) <= 15.5f && MathF.Max(s.Start.X, s.End.X) >= 24.5f);
        Assert.All(moving.Where(s => s.Kind == MoveKind.Rapid), s => Assert.Equal(result.SafeZ, s.Start.Z, 3));
        Assert.Empty(result.Collisions.Report!.Events);

        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        var stock = simulation.Stock!;
        foreach (var x in new[] { 5f, 35f })
        {
            var (i, j) = stock.CellOf(x, 10f);
            Assert.Equal(result.Floor, stock[i, j], 3);
        }

        var (wi, wj) = stock.CellOf(20f, 10f);
        Assert.Equal(top, stock[wi, wj], 3);
    }
}
