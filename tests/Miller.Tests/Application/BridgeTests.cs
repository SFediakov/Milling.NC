using Miller.Application.Services;
using Miller.Core.Generation;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// Holding bridges of the separation scope through the whole pipeline and the simulation: the trench
// frees the box at the stock bottom, the bridges keep it on the frame.
public sealed class BridgeTests
{
    private const float Slack = 1e-3f;
    private const float Center = 20f;

    private static MillingProject BoxProject(string strategy = ZLayerByLayerStrategy.StrategyId)
    {
        var project = MillingProject.Default();
        project.CutScope = CutScope.Separation;
        project.RoutingStrategyId = strategy;
        project.Tool.CutterLength = 20f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.25f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project)
        => new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    private static HeightMap Simulate(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        return simulation.Stock!;
    }

    private static List<int> BridgeCells(PipelineResult result)
        => Enumerable.Range(0, result.Status.Length).Where(k => (result.Status[k] & CellStatus.Bridge) != 0).ToList();

    // 4-connected flood over the cells standing at least `height`: whether the model centre reaches
    // the grid border, i.e. the part still hangs on the frame.
    private static bool HeldByFrame(HeightMap stock, float height)
    {
        var (si, sj) = stock.CellOf(Center, Center);
        var seen = new bool[stock.Width, stock.Height];
        var queue = new Queue<(int I, int J)>();
        queue.Enqueue((si, sj));
        seen[si, sj] = true;
        while (queue.Count > 0)
        {
            var (i, j) = queue.Dequeue();
            if (i == 0 || j == 0 || i == stock.Width - 1 || j == stock.Height - 1)
            {
                return true;
            }

            foreach (var (ni, nj) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
            {
                if (!seen[ni, nj] && stock[ni, nj] >= height - Slack)
                {
                    seen[ni, nj] = true;
                    queue.Enqueue((ni, nj));
                }
            }
        }

        return false;
    }

    // 4-connected components of the bridge cells, each as its cell list.
    private static List<List<int>> Bridges(PipelineResult result, int width)
    {
        var left = BridgeCells(result).ToHashSet();
        var components = new List<List<int>>();
        while (left.Count > 0)
        {
            var first = left.First();
            left.Remove(first);
            var component = new List<int> { first };
            var queue = new Queue<int>(component);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                var i = k % width;
                foreach (var n in new[] { i + 1 < width ? k + 1 : -1, i > 0 ? k - 1 : -1, k + width, k - width })
                {
                    if (n >= 0 && left.Remove(n))
                    {
                        component.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static void AssertBridgesStand(PipelineResult result, HeightMap stock, float height)
    {
        var top = result.Floor + height;
        Assert.True(HeldByFrame(stock, top), "the box is not held by its bridges");
        Assert.All(BridgeCells(result), k => Assert.Equal(top, stock.Z[k], 3));
    }

    [Fact]
    public void FreedBox_KeepsFourBridgesOfTheSetSize_AndHangsOnTheFrame()
    {
        var result = Run(BoxProject());
        Assert.Equal(new BridgeReport(1, 4, 4), result.Bridges);
        Assert.Equal(16, (int)CellStatus.Bridge);
        var stock = Simulate(result);
        AssertBridgesStand(result, stock, MillingProject.DefaultBridgeHeight);

        // One bridge per side, each crossing the band at the middle of its face.
        var bridges = Bridges(result, stock.Width);
        Assert.Equal(4, bridges.Count);
        foreach (var (x, y) in new[] { (28f, Center), (Center, 28f), (12f, Center), (Center, 12f) })
        {
            var (i, j) = stock.CellOf(x, y);
            Assert.Contains(bridges, b => b.Contains(stock.Index(i, j)));
        }

        // The +X bridge at x = 28: as wide as set, rounded up to whole cells; the band is crossed from
        // the box wall (x = 25) to the frame (x = 31) and no further.
        var (bi, _) = stock.CellOf(28f, Center);
        var cells = BridgeCells(result);
        var rows = cells.Where(k => k % stock.Width == bi).Select(k => k / stock.Width).ToList();
        var width = (rows.Max() - rows.Min() + 1) * stock.CellSize;
        Assert.InRange(width, MillingProject.DefaultBridgeWidth, MillingProject.DefaultBridgeWidth + stock.CellSize);
        var (_, bj) = stock.CellOf(28f, Center);
        var columns = cells.Where(k => k / stock.Width == bj).Select(k => stock.OriginX + (k % stock.Width + 0.5f) * stock.CellSize).Where(x => x > Center).ToList();
        Assert.InRange(columns.Min(), 25f, 25.5f);
        Assert.InRange(columns.Max(), 30.5f, 31.5f);

        // Away from the bridges the band is still cut to the floor; the levels gained the bridge top.
        var (oi, oj) = stock.CellOf(28f, Center + 3f);
        Assert.Equal(result.Floor, stock[oi, oj], 3);
        Assert.Contains(result.Plan.Steps, s => MathF.Abs(s.Level - (result.Floor + MillingProject.DefaultBridgeHeight)) < Slack);
    }

    [Fact]
    public void WithoutBridges_TheBoxIsFree_AndThePlanKeepsItsLevels()
    {
        var project = BoxProject();
        project.BridgeCount = 0;
        var result = Run(project);
        Assert.Equal(default, result.Bridges);
        Assert.Empty(BridgeCells(result));
        Assert.False(HeldByFrame(Simulate(result), result.Floor + MillingProject.DefaultBridgeHeight));
        Assert.DoesNotContain(result.Plan.Steps, s => MathF.Abs(s.Level - (result.Floor + MillingProject.DefaultBridgeHeight)) < Slack);
    }

    [Theory]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, TipType.Ball, CollisionMode.OneRun)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, TipType.Flat, CollisionMode.Recursion)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, TipType.Ball, CollisionMode.OneRun)]
    public void EveryStrategyTipAndCollisionMode_KeepsTheBridges(string strategy, TipType tip, CollisionMode mode)
    {
        var project = BoxProject(strategy);
        project.Tool.TipType = tip;
        project.CollisionMode = mode;
        var result = Run(project);
        Assert.Equal(new BridgeReport(1, 4, 4), result.Bridges);
        // The plan holds the bridge top at cell centres; between them a ball dips by at most the
        // project tolerance, the rule the model surface follows as well.
        var stock = Simulate(result);
        var lowest = result.Floor + MillingProject.DefaultBridgeHeight - (tip == TipType.Ball ? result.Tolerance : 0f);
        Assert.True(HeldByFrame(stock, lowest), "the box is not held by its bridges");
        Assert.All(BridgeCells(result), k => Assert.True(stock.Z[k] >= lowest - Slack, $"bridge cell {k} cut to {stock.Z[k]}"));
    }

    // A curved part: the heart laid flat in a 6 mm stock is freed at the floor all around; its four
    // bridges stand at the set height and hold it, without them it falls out.
    [Fact]
    public void HeartLaidFlat_HangsOnItsBridges()
    {
        var project = MillingProject.Default();
        project.CutScope = CutScope.Separation;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 6;
        project.Axes.MapY = ModelAxis.Z;
        project.Axes.MapZ = ModelAxis.Y;
        project.Tool.CutterLength = 20f;
        project.Parameters.CellSize = 0.2f;
        project.Models.Add(new ModelPlacement { StlPath = TestMeshes.FixtureFileName });
        var import = new MeshImportService();
        import.Import(TestMeshes.FixturePath());

        var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        Assert.Equal(new BridgeReport(1, 4, 4), result.Bridges);
        var stock = Simulate(result);
        Assert.Equal(4, Bridges(result, stock.Width).Count);
        AssertBridgesStand(result, stock, MillingProject.DefaultBridgeHeight);

        project.BridgeCount = 0;
        var free = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        Assert.False(HeldByFrame(Simulate(free), free.Floor + MillingProject.DefaultBridgeHeight));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheCount_IsPerFreedPart(int count)
    {
        var project = BoxProject();
        project.BridgeCount = count;
        var result = Run(project);
        Assert.Equal(new BridgeReport(1, count, count), result.Bridges);
        var stock = Simulate(result);
        Assert.Equal(count, Bridges(result, stock.Width).Count);
        AssertBridgesStand(result, stock, MillingProject.DefaultBridgeHeight);
    }

    // A height between two levels gets its own level, so the layer strategy cuts the bridge to it.
    [Fact]
    public void AHigherBridge_StandsAtItsHeight()
    {
        var project = BoxProject();
        project.BridgeHeight = 1.4f;
        var result = Run(project);
        AssertBridgesStand(result, Simulate(result), 1.4f);
    }

    [Fact]
    public void EverythingScope_PlacesNoBridges()
    {
        var project = BoxProject();
        project.CutScope = CutScope.Everything;
        var result = Run(project);
        Assert.Equal(default, result.Bridges);
        Assert.Empty(BridgeCells(result));
    }

    // A box flush with the stock side is never freed: the frame holds it, no bridge is needed.
    [Fact]
    public void APartThatTouchesTheStockSide_NeedsNoBridge()
    {
        var project = BoxProject();
        project.Stock.AlignX = StockAlignment.Min;
        var result = Run(project);
        Assert.Equal(default, result.Bridges);
        Assert.Empty(BridgeCells(result));
    }

    // The native library checks its arguments itself; the validator is not the only guard.
    [Fact]
    public void TheNativeLibrary_RejectsACountOutOfRange()
    {
        var project = BoxProject();
        project.BridgeCount = MillingProject.MaxBridgeCount + 1;
        var error = Assert.Throws<ArgumentException>(() => ToolpathGeneration.Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None));
        Assert.Contains("bridge count", error.Message);
    }
}
