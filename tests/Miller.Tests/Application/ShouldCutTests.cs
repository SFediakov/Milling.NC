using Miller.Application.Services;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-136: stock above the model that blocks the head becomes should be cut, the head limit counts it
// at its closing plus the tolerance, and the strategies cut it there before the tool goes deeper.
// The fixture is an 8 x 8 box in a 14 x 14 x 5 stock under a 2 mm cutter with a 4 mm head.
public sealed class ShouldCutTests
{
    private const int MaxHeadIterations = 8;

    private static MillingProject BoxProject(float boxHeight, float cutterLength, float stepdown, string strategy, StockAlignment alignZ)
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Tool.CutterLength = cutterLength;
        project.Parameters.Stepover = 1;
        project.Parameters.Stepdown = stepdown;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 5;
        project.Stock.AlignZ = alignZ;
        project.Parameters.CellSize = 0.5f;
        project.RoutingStrategyId = strategy;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project, float boxHeight)
        => new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, boxHeight) }, null, CancellationToken.None);

    private static int Count(bool[,] mask) => mask.Cast<bool>().Count(b => b);

    // The head-limited mask of the loop without feedback: every cell counts with its closing rounded
    // up to the level it stands at, iterated from the tip map until the effective tip settles.
    private static bool[,] HeadLimitedWithoutFeedback(PipelineResult result, MillingProject project)
    {
        var effective = result.Tip.Clone();
        var limit = effective;
        for (var iteration = 0; iteration < MaxHeadIterations; iteration++)
        {
            var remaining = Slicer.CeilToLevels(HeightMapDilation.ComputeRemaining(effective, result.Profile), result.Stock.StockTop, project.Parameters.Stepdown);
            limit = HeadClearance.ComputeHeadLimit(remaining, result.Profile, project.Tool.CutterLength);
            var next = HeadClearance.ApplyHeadLimit(result.Tip, limit);
            var settled = true;
            for (var j = 0; j < next.Height && settled; j++)
            {
                for (var i = 0; i < next.Width; i++)
                {
                    var a = next[i, j];
                    var b = effective[i, j];
                    if (float.IsNaN(a) != float.IsNaN(b) || MathF.Abs(a - b) > project.Parameters.Tolerance)
                    {
                        settled = false;
                        break;
                    }
                }
            }

            effective = next;
            if (settled)
            {
                break;
            }
        }

        return HeadClearance.HeadLimitedMask(result.Tip, limit, project.Parameters.Tolerance);
    }

    private static SimulationService Simulated(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        return simulation;
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void BlockingStock_BecomesShouldCut_IsRemoved_AndTheHeadLimitShrinks(string strategy)
    {
        var project = BoxProject(3, 2.5f, 1.5f, strategy, StockAlignment.Max);
        var result = Run(project, 3);
        var shouldCut = Count(result.ShouldCut);
        Assert.True(shouldCut > 0);
        var limited = Count(result.HeadLimitedMask);
        var before = Count(HeadLimitedWithoutFeedback(result, project));
        Assert.True(limited < before, $"head-limited {limited} cells, {before} without feedback");

        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        var simulation = Simulated(result);
        Assert.Empty(simulation.Events);

        // Every should-cut cell was stock above the model and ends at its closing within the tolerance.
        var closing = HeightMapDilation.ComputeRemaining(result.EffectiveTip, result.Profile);
        var stock = simulation.Stock!;
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                if (!result.ShouldCut[i, j])
                {
                    continue;
                }

                Assert.True(result.Model[i, j] < result.Stock.StockTop, $"({i},{j}) is model at the stock top");
                Assert.True(stock[i, j] <= closing[i, j] + result.Tolerance + CollisionDetector.Tolerance, $"({i},{j}) stands at {stock[i, j]}, closing {closing[i, j]}");
            }
        }
    }

    [Fact]
    public void ShouldCutStockBetweenTheStockTopAndTheFirstLevel_IsCutByTheLayerStrategy()
    {
        // The box top (4.2) lies between the first level (3) and the stock top (5), in no cave.
        var project = BoxProject(4.2f, 2.5f, 2f, ZLayerByLayerStrategy.StrategyId, StockAlignment.Min);
        var result = Run(project, 4.2f);
        Assert.True(Count(result.ShouldCut) > 0);
        var firstLevel = result.Stock.StockTop - project.Parameters.Stepdown;
        Assert.Contains(result.Toolpath.Segments, s => s.Kind == MoveKind.Feed && s.End.Z > firstLevel + 0.1f && s.End.Z < result.Stock.StockTop - 0.1f);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        Assert.Empty(Simulated(result).Events);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId)]
    public void WithoutBlockingStock_NothingIsShouldCut_AndTheHeadLimitIsUnchanged(string strategy)
    {
        // A 10 mm box in a 40 x 40 x 30 stock under a 12 mm cutter: the model alone limits the head.
        var project = MillingProject.Default();
        project.Tool.CutterLength = 12f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 30;
        project.Parameters.CellSize = 0.5f;
        project.RoutingStrategyId = strategy;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        var result = new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);
        Assert.Equal(0, Count(result.ShouldCut));
        Assert.True(Count(result.HeadLimitedMask) > 0);
        Assert.Equal(HeadLimitedWithoutFeedback(result, project).Cast<bool>(), result.HeadLimitedMask.Cast<bool>());
    }
}
