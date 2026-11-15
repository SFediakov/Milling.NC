using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Generation;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The collision handling left after T-214 (user request: Z layer by layer with Backwards on the CPU or
// the GPU only): the backward check leaves out the positions whose head would meet the model, so the
// generation ends in one pass without a far stepdown. Recursion, One run and 3 axis freedom are gone end
// to end; the native library refuses their numbers. The fixture is an 8 x 8 x 3 box at the top of a
// 14 x 14 x 5 stock under a 2 mm cutter with a 4 mm head and a 2.5 mm cutter length: the head meets the
// box top from the floor around it.
public sealed class CollisionModesTests
{
    private static MillingProject BoxProject()
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

    [Fact]
    public void TheModesLeft_AreBackwardsOnTheCpuAndOnTheGpu_WithBackwardsTheDefault()
    {
        Assert.Equal(new[] { CollisionMode.Backwards, CollisionMode.Backwards_GPU }, Enum.GetValues<CollisionMode>());
        Assert.Equal(0, (int)CollisionMode.Backwards);
        Assert.Equal(1, (int)CollisionMode.Backwards_GPU);
        Assert.Equal(CollisionMode.Backwards, MillingProject.Default().CollisionMode);
        Assert.Equal(CollisionMode.Backwards, new MillingPreset().CollisionMode);
    }

    // The head meets the box top from the floor around it at the deep levels: the backward check leaves
    // those positions out, so one pass ends without collisions in the check and in the simulation.
    [Fact]
    public void BoxBesideWall_EndsInOnePass_WithoutCollisions_InTheCheckAndInTheSimulation()
    {
        var result = Run(BoxProject());
        Assert.True(result.Collisions.Succeeded);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Empty(Simulated(result));
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        Assert.Equal(0, Count(result, CellStatus.Collision));
        Assert.Equal(1, result.Passes);
        Assert.NotNull(result.Unreached);
        Assert.True(result.Unreached!.Value.Unreached > 0, "the head meets the box top beside it, so some positions are unreached");
    }

    // A project without blocking material: one pass, nothing unreached, and the head limit maps hold nothing.
    [Fact]
    public void WithoutCollisions_OnePass_NothingUnreached()
    {
        var project = BoxProject();
        project.Tool.CutterLength = 20f;
        var result = Run(project);
        Assert.Equal(1, result.Passes);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.Equal(0, Count(result, CellStatus.Collision));
        Assert.Equal(0, result.Unreached!.Value.Unreached);
        Assert.All(result.HeadLimit.Z, z => Assert.True(float.IsNaN(z)));
        Assert.DoesNotContain(result.HeadLimitedMask.Cast<bool>(), b => b);
        Assert.Equal(result.Tip.Z, result.EffectiveTip.Z);
    }

    // The numbers Recursion (0 before), One run (1) and the GPU mode had before T-214 renumbered the enum;
    // anything outside the two modes is refused by the validator and by the native library itself.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void AModeOutsideTheTwo_IsRefused_ByTheValidatorAndTheNativeLibrary(int number)
    {
        var project = BoxProject();
        project.CollisionMode = (CollisionMode)number;
        Assert.Contains(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Strategy.CollisionMode");
        var error = Assert.Throws<ArgumentException>(() => ToolpathGeneration.Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None));
        Assert.Contains($"Unknown collision mode {number}", error.Message);
    }

    [Fact]
    public void AStrategyOtherThanTheZLayer_IsRefused_BeforeTheNativeLibrary()
    {
        var project = BoxProject();
        project.RoutingStrategyId = "three-axis-freedom";
        var error = Assert.Throws<ArgumentException>(() => ToolpathGeneration.Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None));
        Assert.Contains("three-axis-freedom", error.Message);
    }
}
