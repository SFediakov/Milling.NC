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

    // A truncated pyramid turned by 45 degrees with its flat top flush with the stock top: the routes
    // along its sloped edge step diagonally past the corners of top cells. Such a corner used to lift
    // the tool onto the top plane itself; a point that has to rise onto material standing at the stock
    // top now takes the safe height, and a corner of stock the footprints cut anyway lifts nothing.
    private static PipelineResult RunFlushPyramid(string strategy, float safeHeight)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.Parameters.SafeHeight = safeHeight;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 6;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "pyramid.stl", RotationZ = 45 });
        return new PipelineService().Run(project, new[] { TruncatedPyramid(24f, 10f, 6f) }, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, 5f)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, 5f)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, 2f)]
    public void FlushModelTop_NoMoveRunsBetweenTheStockTopAndTheSafePlane(string strategy, float safeHeight)
    {
        var result = RunFlushPyramid(strategy, safeHeight);
        var top = result.Stock.StockTop;
        Assert.Equal(top + safeHeight, result.SafeZ, 4);
        var near = result.Toolpath.Segments.Where(s => Vector2.Distance(new Vector2(s.Start.X, s.Start.Y), new Vector2(s.End.X, s.End.Y)) > 1e-3f)
            .Where(s => MathF.Max(s.Start.Z, s.End.Z) >= top - 1e-3f && MathF.Max(s.Start.Z, s.End.Z) < result.SafeZ - 1e-3f)
            .ToList();
        Assert.True(near.Count == 0, $"{near.Count} moves between the stock top and the safe plane, first {near.FirstOrDefault()}");
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
    }

    // Square frustum: `bottom` wide at z = 0, `top` wide at z = height, centred.
    private static Miller.Core.Geometry.Mesh TruncatedPyramid(float bottom, float top, float height)
    {
        var inset = (bottom - top) / 2f;
        var p = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var upper = (i & 4) != 0;
            var lo = upper ? inset : 0f;
            var hi = upper ? inset + top : bottom;
            p[i] = new Vector3((i & 1) == 0 ? lo : hi, (i & 2) == 0 ? lo : hi, upper ? height : 0f);
        }

        var t = new List<Miller.Core.Geometry.Triangle>(12);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            t.Add(new Miller.Core.Geometry.Triangle(a, b, c));
            t.Add(new Miller.Core.Geometry.Triangle(a, c, d));
        }

        Quad(p[0], p[2], p[3], p[1]);
        Quad(p[4], p[5], p[7], p[6]);
        Quad(p[0], p[1], p[5], p[4]);
        Quad(p[2], p[6], p[7], p[3]);
        Quad(p[0], p[4], p[6], p[2]);
        Quad(p[1], p[3], p[7], p[5]);
        return new Miller.Core.Geometry.Mesh(t);
    }
}
