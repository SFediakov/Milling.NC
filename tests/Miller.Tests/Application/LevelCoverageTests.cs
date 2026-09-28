using Miller.Application.Services;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// Every cell a level works on lies under the cutter of one of its nodes. A node lattice wider than
// the cutter radius times sqrt(2) left cells no node reached; where the route did not pass over them
// the stock stood as a spike through every level and the head of the deeper levels met it. The
// fixture is a full-height 8 x 8 box in a 30 x 30 x 6 stock under a 3 mm cutter: everything outside
// the box is cut to the floor, so the simulated stock must end at the planned closing everywhere.
public sealed class LevelCoverageTests
{
    private static PipelineResult Run(string strategy, float stepover, CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.CollisionMode = mode;
        project.Tool.CutterDiameter = 3f;
        project.Tool.HeadDiameter = 6f;
        project.Parameters.Stepover = stepover;
        project.Parameters.FinishingStepover = stepover;
        project.Stock.SizeX = 30;
        project.Stock.SizeY = 30;
        project.Stock.SizeZ = 6;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 6) }, null, CancellationToken.None);
    }

    // The stepover equals the cutter diameter (the largest the validator accepts): before the fix 19
    // cells stood above the plan, up to one stepdown high.
    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Recursion)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.OneRun)]
    [InlineData(ThreeAxisFreedomStrategy.StrategyId, CollisionMode.Recursion)]
    public void StepoverOfTheCutterDiameter_LeavesNoStockAboveThePlan(string strategy, CollisionMode mode)
    {
        var result = Run(strategy, 3f, mode);
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Empty(simulation.Events);
        var stock = simulation.Stock!;
        var closing = HeightMapDilation.ComputeRemaining(result.EffectiveTip, result.Profile);
        var slack = result.Tolerance + CollisionDetector.Tolerance;
        var above = 0;
        for (var k = 0; k < stock.Z.Length; k++)
        {
            if (!float.IsNaN(closing.Z[k]) && stock.Z[k] > closing.Z[k] + slack)
            {
                above++;
            }
        }

        Assert.Equal(0, above);
    }
}
