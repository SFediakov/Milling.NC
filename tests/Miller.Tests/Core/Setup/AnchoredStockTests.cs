using System.Numerics;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

// The stock is anchored to the models before their offsets: an offset moves a model inside the
// stock (also for one model in an auto-fit stock), the stock box in machine space depends on the
// origin mode only, and every consumer of the layout agrees on it.
public sealed class AnchoredStockTests
{
    private static MillingProject Project(params ModelPlacement[] placements)
    {
        var project = MillingProject.Default();
        project.Models.AddRange(placements);
        return project;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void LoneModel_OffsetMovesTheModel_AndTheStockStays()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        var bounds = new[] { mesh.Bounds };
        var before = ModelLayout.MachineBounds(project, bounds);
        var stockBefore = ModelLayout.StockBoundsMachine(project);

        project.Models[0].Offset = new Vector3(5, -3, 2);
        var after = ModelLayout.MachineBounds(project, bounds);
        AssertClose(before.Min + new Vector3(5, -3, 2), after.Min);
        AssertClose(before.Max + new Vector3(5, -3, 2), after.Max);
        Assert.Equal(stockBefore, ModelLayout.StockBoundsMachine(project));
        Assert.Equal(new BoundingBox(Vector3.Zero, new Vector3(100, 100, 30)), stockBefore);
    }

    [Fact]
    public void ZeroOffsets_LandWhereTheStockRuleAlonePutsThem()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        var machine = ModelLayout.MachineBounds(project, new[] { mesh.Bounds });
        // Centred in a 100 x 100 stock, top at the 30 mm stock top.
        Assert.Equal(45f, machine.Min.X, 3);
        Assert.Equal(45f, machine.Min.Y, 3);
        Assert.Equal(25f, machine.Min.Z, 3);
        Assert.Equal(30f, machine.Max.Z, 3);
    }

    [Fact]
    public void StockBoundsMachine_FollowsTheOriginModeOnly()
    {
        var project = Project(new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(40, 0, 0) });
        Assert.Equal(Vector3.Zero, ModelLayout.StockBoundsMachine(project).Min);
        project.Axes.OriginMode = OriginMode.StockCenterTopZ;
        Assert.Equal(new Vector3(-50, -50, -30), ModelLayout.StockBoundsMachine(project).Min);
        project.Axes.OriginMode = OriginMode.Custom;
        project.Axes.CustomOffset = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(-1, -2, -3), ModelLayout.StockBoundsMachine(project).Min);
        Assert.Equal(new Vector3(100, 100, 30), ModelLayout.StockBoundsMachine(project).Size);
    }

    [Fact]
    public void PipelineStock_ViewportStock_AndValidator_AgreeOnTheStockBox()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(60, 0, 0) });
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 10;
        var bounds = new[] { mesh.Bounds };
        var stock = StockModel.Create(project.Stock, ModelLayout.AnchorBoundsMachine(project, bounds), 0.5f);
        var expected = ModelLayout.StockBoundsMachine(project);
        Assert.Equal(expected.Min.X, stock.Bounds.Min.X, 4);
        Assert.Equal(expected.Min.Y, stock.Bounds.Min.Y, 4);
        Assert.Equal(expected.Min.Z, stock.Bounds.Min.Z, 4);
        Assert.Equal(expected.Max.Z, stock.StockTop, 4);

        // The offset pushed the model out of the 40 mm stock: the validator says so, and it does not
        // once the offset is gone.
        var machine = ModelLayout.MachineBounds(project, bounds);
        Assert.True(machine.Max.X > expected.Max.X);
        var result = ProjectValidator.Validate(project, machine);
        Assert.Contains(result.Warnings, w => w.Field == "Stock.Placement");
        project.Models[0].Offset = Vector3.Zero;
        Assert.DoesNotContain(ProjectValidator.Validate(project, ModelLayout.MachineBounds(project, bounds)).Warnings, w => w.Field == "Stock.Placement");
    }

    [Fact]
    public void RotationAndOrientation_ShapeTheAnchor_OffsetsDoNot()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", RotationZ = 90f, Offset = new Vector3(7, 7, 0) });
        var anchor = ModelLayout.AnchorBounds(project, new[] { mesh.Bounds });
        Assert.Equal(4f, anchor.Size.X, 3);
        Assert.Equal(10f, anchor.Size.Y, 3);
        project.Models[0].Offset = Vector3.Zero;
        var unmoved = ModelLayout.AnchorBounds(project, new[] { mesh.Bounds });
        AssertClose(unmoved.Min, anchor.Min);
        AssertClose(unmoved.Max, anchor.Max);
    }

    [Fact]
    public void AlignedOffset_AlwaysReachesTheStockExtreme_InOneStep()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(3, -8, 1) });
        var bounds = new[] { mesh.Bounds };
        foreach (var alignment in new[] { StockAlignment.Min, StockAlignment.Center, StockAlignment.Max })
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var offset = ModelLayout.AlignedOffset(project, bounds, 0, axis, alignment);
                var original = project.Models[0].Offset;
                project.Models[0].Offset = offset;
                var machine = ModelLayout.MachineBounds(project, bounds);
                var stock = ModelLayout.StockBoundsMachine(project);
                var (stockPoint, modelPoint) = alignment switch
                {
                    StockAlignment.Min => (stock.Min, machine.Min),
                    StockAlignment.Center => (stock.Center, machine.Center),
                    _ => (stock.Max, machine.Max),
                };
                var expected = axis switch { 0 => stockPoint.X, 1 => stockPoint.Y, _ => stockPoint.Z };
                var actual = axis switch { 0 => modelPoint.X, 1 => modelPoint.Y, _ => modelPoint.Z };
                Assert.Equal(expected, actual, 3);
                // The other two components of the offset are untouched.
                for (var other = 0; other < 3; other++)
                {
                    if (other != axis)
                    {
                        Assert.Equal(other switch { 0 => original.X, 1 => original.Y, _ => original.Z }, other switch { 0 => offset.X, 1 => offset.Y, _ => offset.Z });
                    }
                }

                project.Models[0].Offset = original;
            }
        }
    }

    [Fact]
    public void TwoModels_KeepTheirRelativePlacement_AndTheAnchorIsTheirUnionAtZeroOffset()
    {
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(-15, 0, 0) }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(15, 0, 0) });
        var meshes = ModelLayout.MachineMeshes(project, new[] { a, b });
        Assert.Equal(meshes[0].Bounds.Min.X + 30f, meshes[1].Bounds.Min.X, 3);
        // Symmetric offsets around the anchored centre: the pair is centred in the 100 mm stock.
        var union = ModelLayout.MachineBounds(project, new[] { a.Bounds, b.Bounds });
        Assert.Equal(50f, union.Center.X, 3);
        Assert.Equal(40f, union.Size.X, 3);
    }
}
