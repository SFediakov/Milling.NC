using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The far stepdown through the pipeline: the same final stock as without it, no gouge, no collision
// event in either collision mode, the layers of the far pass and the normal levels, and no effect on
// "3 axis freedom".
public sealed class FarStepdownPipelineTests
{
    private static PipelineResult Run(string strategy, float farStepdown, CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.CollisionMode = mode;
        project.Parameters.FarStepdown = farStepdown;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);
    }

    private static SimulationService Finished(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        return simulation;
    }

    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void ZLayer_EndsAtThePlainStock_WithoutGougesOrEvents(CollisionMode mode)
    {
        var far = Run(ZLayerByLayerStrategy.StrategyId, 4f, mode);
        var plain = Run(ZLayerByLayerStrategy.StrategyId, 0f, mode);
        Assert.Empty(GougeChecker.Verify(far.Toolpath, far.EffectiveTip, far.Tolerance));
        var farRun = Finished(far);
        var plainRun = Finished(plain);
        Assert.Empty(farRun.Events);
        Assert.Empty(plainRun.Events);
        var slack = 2 * far.Tolerance + CollisionDetector.Tolerance;
        for (var k = 0; k < far.Stock.Map.Z.Length; k++)
        {
            Assert.True(MathF.Abs(farRun.Stock!.Z[k] - plainRun.Stock!.Z[k]) <= slack, $"cell {k}: {farRun.Stock.Z[k]} against {plainRun.Stock.Z[k]}");
        }

        Assert.Equal(new[] { 3f, 1f, 0f }, far.Plan.Steps.Select(s => s.Level));
        Assert.Equal(new[] { 1f, 3f, 1f, 0f }, far.Layers.Select(l => l.Level));
        Assert.Equal(new[] { 3f, 1f, 0f }, plain.Layers.Select(l => l.Level));
    }

    [Fact]
    public void ThreeAxisFreedom_IgnoresTheFarStepdown()
    {
        var far = Run(ThreeAxisFreedomStrategy.StrategyId, 4f, CollisionMode.Recursion);
        var plain = Run(ThreeAxisFreedomStrategy.StrategyId, 0f, CollisionMode.Recursion);
        Assert.Equal(plain.Toolpath.Segments, far.Toolpath.Segments);
    }
}
