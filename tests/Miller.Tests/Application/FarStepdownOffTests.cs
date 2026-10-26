using Miller.Application.Services;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-196 (user request): a far stepdown equal to the stepdown switches the far stepdown off. Every strategy
// with every collision mode it takes and both cut scopes then mills exactly as with 0: the same toolpath,
// layers, passes, far areas, unreached and not-cut counts.
public sealed class FarStepdownOffTests
{
    private static PipelineResult Run(string strategy, CollisionMode mode, CutScope scope, float farStepdown)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.CollisionMode = mode;
        project.CutScope = scope;
        project.Parameters.FarStepdown = farStepdown;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Backwards, CutScope.Everything)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Backwards, CutScope.Separation)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Backwards_GPU, CutScope.Everything)]
    [InlineData(ZLayerByLayerStrategy.StrategyId, CollisionMode.Backwards_GPU, CutScope.Separation)]
    public void TheStepdownAsFarStepdown_MillsAsWithoutIt(string strategy, CollisionMode mode, CutScope scope)
    {
        Assert.SkipWhen(mode == CollisionMode.Backwards_GPU && !GpuDevice.Available, $"No OpenCL GPU: {GpuDevice.Description}");
        var stepdown = CuttingParameters.Default().Stepdown;
        var off = Run(strategy, mode, scope, 0f);
        var equal = Run(strategy, mode, scope, stepdown);
        Assert.Equal(off.Toolpath.Segments, equal.Toolpath.Segments);
        Assert.Equal(off.Layers, equal.Layers);
        Assert.Equal(off.Passes, equal.Passes);
        Assert.Equal(off.RouteUnitEntries, equal.RouteUnitEntries);
        Assert.Equal(off.Unreached, equal.Unreached);
        Assert.Equal(off.Uncut, equal.Uncut);
        Assert.Equal(off.Collisions.Report!.Events.Count, equal.Collisions.Report!.Events.Count);
        foreach (var area in new[] { equal.FarStepdown.FirstArea, equal.FarStepdown.Milled, equal.FarStepdown.Withdrawn })
        {
            Assert.DoesNotContain(true, area.Cast<bool>());
        }
    }
}
