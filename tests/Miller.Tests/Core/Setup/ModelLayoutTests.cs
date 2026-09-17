using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

public sealed class ModelLayoutTests
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
    public void OneModelWithoutPlacement_LandsWhereAxisSetupPutIt()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        project.Axes.FlipY = true;
        project.Axes.RotationZ = 30f;
        var expected = mesh.Transform(project.Axes.ToMatrix(mesh.Bounds, project.Stock)).Bounds;
        var actual = ModelLayout.MachineBounds(project, new[] { mesh.Bounds });
        AssertClose(expected.Min, actual.Min);
        AssertClose(expected.Max, actual.Max);
        Assert.Single(ModelLayout.MachineMatrices(project, new[] { mesh.Bounds }));
    }

    [Fact]
    public void Offset_MovesTheSecondModelRelativeToTheFirst()
    {
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(20, 0, 0) });
        var meshes = ModelLayout.MachineMeshes(project, new[] { a, b });
        Assert.Equal(2, meshes.Count);
        AssertClose(meshes[0].Bounds.Min + new Vector3(20, 0, 0), meshes[1].Bounds.Min);
        var union = ModelLayout.MachineBounds(project, new[] { a.Bounds, b.Bounds });
        Assert.Equal(30f, union.Size.X, 3);
        Assert.Equal(10f, union.Size.Y, 3);
        // Auto-fit stock anchored to the boxes at zero offset (both at 0..10): the stock is centred on
        // that anchor, so the first box starts 45 mm in and the second, offset by 20, at 65.
        Assert.Equal(45f, meshes[0].Bounds.Min.X, 3);
        Assert.Equal(45f, meshes[0].Bounds.Min.Y, 3);
        Assert.Equal(2 * a.TriangleCount, ModelLayout.MergeMachineMeshes(project, new[] { a, b }).TriangleCount);
    }

    [Fact]
    public void RotationAboutZ_TurnsAroundTheModelCenter()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", RotationZ = 90f });
        var placed = AxisSetup.TransformBounds(mesh.Bounds, ModelLayout.PlacementMatrix(project.Axes, project.Models[0], mesh.Bounds));
        Assert.Equal(4f, placed.Size.X, 3);
        Assert.Equal(10f, placed.Size.Y, 3);
        Assert.Equal(5f, placed.Size.Z, 3);
        AssertClose(mesh.Bounds.Center, placed.Center);
    }

    [Fact]
    public void CenteredOffset_PutsTheModelInTheStockMiddleOnOneAxisOnly()
    {
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(20, 30, 0) });
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = Vector3.Zero;
        var bounds = new[] { a.Bounds, b.Bounds };
        // Stock corner at the anchor minimum (both boxes at 0..10 before their offsets), size
        // 100 x 100 x 30, so the stock middle is 50 whatever the offsets: model 0 (centre 5) needs 45,
        // model 1 (centre 35 on Y after its offset 30) needs 30 + 15.
        var centered = ModelLayout.CenteredOffset(project, bounds, 0, 0);
        Assert.Equal(45f, centered.X, 3);
        Assert.Equal(0f, centered.Y, 3);
        Assert.Equal(0f, centered.Z, 3);
        var centeredY = ModelLayout.CenteredOffset(project, bounds, 1, 1);
        Assert.Equal(20f, centeredY.X, 3);
        Assert.Equal(30f + (50f - 35f), centeredY.Y, 3);
        var centeredZ = ModelLayout.CenteredOffset(project, bounds, 1, 2);
        Assert.Equal(15f - 2.5f, centeredZ.Z, 3);
    }

    [Fact]
    public void MismatchedCounts_AreRejected()
    {
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        Assert.Throws<ArgumentException>(() => ModelLayout.MachineBounds(project, new[] { TestMeshes.UnitCube().Bounds, TestMeshes.UnitCube().Bounds }));
        Assert.Throws<ArgumentException>(() => ModelLayout.MachineBounds(project, Array.Empty<BoundingBox>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelLayout.CenteredOffset(project, new[] { TestMeshes.UnitCube().Bounds }, 0, 3));
    }
}
