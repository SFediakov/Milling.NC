using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-165: a head bottom down to half the cutter diameter. A cylinder head inside the cutter radius never
// reaches past the cutter, and the flutes cannot cut above themselves, so it generates exactly as a
// cylinder of the cutter diameter does: in both cut scopes, with and without a far stepdown.
public sealed class NarrowHeadTests
{
    private static MillingProject Project(float headDiameter, CutScope scope, float farStepdown)
    {
        var project = MillingProject.Default();
        project.CutScope = scope;
        project.Tool.CutterLength = 3f;
        project.Tool.HeadDiameter = headDiameter;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.CellSize = 0.5f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 8;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project)
        => new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 6) }, null, CancellationToken.None);

    [Theory]
    [InlineData(CutScope.Everything, 0f)]
    [InlineData(CutScope.Everything, 4f)]
    [InlineData(CutScope.Separation, 0f)]
    [InlineData(CutScope.Separation, 4f)]
    public void CylinderHeadInsideTheCutter_GeneratesAsACylinderOfTheCutterDiameter(CutScope scope, float farStepdown)
    {
        var narrow = Project(3f, scope, farStepdown);
        var flush = Project(6f, scope, farStepdown);
        Assert.True(ProjectValidator.Validate(narrow, TestMeshes.Box(10, 10, 6).Bounds).IsValid);
        Assert.True(ProjectValidator.Validate(flush, TestMeshes.Box(10, 10, 6).Bounds).IsValid);

        var a = Run(narrow);
        var b = Run(flush);
        Assert.Empty(a.Profile.AnnulusOffsets);
        Assert.True(a.Toolpath.Count > 0);
        Assert.Equal(b.Toolpath.Count, a.Toolpath.Count);
        for (var k = 0; k < a.Toolpath.Count; k++)
        {
            Assert.Equal(b.Toolpath.Segments[k], a.Toolpath.Segments[k]);
        }

        Assert.Equal(b.Collisions.Report!.Events.Count, a.Collisions.Report!.Events.Count);
    }

    // A frustum whose bottom is half the cutter, in separation scope with a far stepdown: no gouge, and the
    // simulation panel finds the events the generation reported.
    [Fact]
    public void FrustumNarrowerThanTheCutter_RunsWithoutGouges_AndTheSimulationAgrees()
    {
        var project = Project(3f, CutScope.Separation, 4f);
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadTopDiameter = 12f;
        project.Tool.HeadLength = 6f;
        Assert.True(ProjectValidator.Validate(project, TestMeshes.Box(10, 10, 6).Bounds).IsValid);

        var result = Run(project);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Equal(result.Collisions.Report!.Events.Select(e => (e.SegmentIndex, e.Kind)), simulation.Events.Select(e => (e.SegmentIndex, e.Kind)));
    }
}
