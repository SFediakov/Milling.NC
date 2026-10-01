using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

// Scale per axis of one placement: factors along the oriented model axes, around the oriented
// center, before the turn about Z, and reaching every consumer through the layout matrices.
public sealed class ModelScaleTests
{
    private const float DegToRad = MathF.PI / 180f;

    private static MillingProject Project(params ModelPlacement[] placements)
    {
        var project = MillingProject.Default();
        project.Models.AddRange(placements);
        return project;
    }

    private static BoundingBox Placed(MillingProject project, Mesh mesh, int index = 0)
        => AxisSetup.TransformBounds(mesh.Bounds, ModelLayout.PlacementMatrix(project.Axes, project.Models[index], mesh.Bounds));

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void NewPlacement_HasFactorOne_AndIsLinked()
    {
        var placement = new ModelPlacement();
        Assert.Equal(Vector3.One, placement.Scale);
        Assert.True(placement.LinkedScale);
    }

    [Fact]
    public void Scale_MultipliesEachAxis_AroundTheModelCenter()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Scale = new Vector3(2, 3, 0.5f) });
        var placed = Placed(project, mesh);
        AssertClose(new Vector3(20, 12, 2.5f), placed.Size);
        AssertClose(mesh.Bounds.Center, placed.Center);
    }

    // Scaling after the turn would shear a turned model; the factor belongs to the model's own axis.
    [Fact]
    public void Scale_StretchesTheModelAxis_BeforeTheTurnAboutZ()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Scale = new Vector3(3, 1, 1), RotationZ = 90f });
        var placed = Placed(project, mesh);
        AssertClose(new Vector3(10, 30, 5), placed.Size);

        project.Models[0].RotationZ = 45f;
        var turned = Placed(project, mesh);
        // A 30 x 10 rectangle turned by 45 degrees: both extents are (30 + 10) / sqrt(2).
        var diagonal = 40f / MathF.Sqrt(2f);
        Assert.Equal(diagonal, turned.Size.X, 2);
        Assert.Equal(diagonal, turned.Size.Y, 2);
    }

    [Fact]
    public void Scale_FollowsTheAxisOrientation()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Scale = new Vector3(2, 1, 1) });
        project.Axes.MapX = ModelAxis.Y;
        project.Axes.MapY = ModelAxis.X;
        // Oriented model: 4 along X, 10 along Y; the X factor acts on the oriented X.
        AssertClose(new Vector3(8, 10, 5), Placed(project, mesh).Size);
    }

    // Factor one adds only an identity to the chain: the matrices equal the chain without scale.
    [Fact]
    public void FactorOne_GivesTheMatricesOfAPlacementWithoutScale()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", RotationZ = 30f, Offset = new Vector3(3, -2, 1) });
        project.Axes.FlipY = true;
        var orientation = project.Axes.ToOrientationMatrix();
        var center = AxisSetup.TransformBounds(mesh.Bounds, orientation).Center;
        var expected = orientation * (Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationZ(30f * DegToRad) * Matrix4x4.CreateTranslation(center));
        Assert.True(expected == ModelLayout.TurnMatrix(project.Axes, project.Models[0], mesh.Bounds));
    }

    [Fact]
    public void Scale_GrowsTheAnchor_SoTheAutoFitStockStaysAroundTheModel()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Scale = new Vector3(2, 2, 1) });
        var bounds = new[] { mesh.Bounds };
        AssertClose(new Vector3(20, 20, 5), ModelLayout.AnchorBounds(project, bounds).Size);
        var machine = ModelLayout.MachineBounds(project, bounds);
        Assert.True(ModelLayout.StockBoundsMachine(project).Contains(machine));
        AssertClose(new Vector3(20, 20, 5), machine.Size);
    }

    [Fact]
    public void NonUniformScale_KeepsUnitNormalsPointingOutward()
    {
        var mesh = TestMeshes.Box(10, 4, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl", Scale = new Vector3(2, 0.5f, 3), RotationZ = 20f });
        var placed = Assert.Single(ModelLayout.MachineMeshes(project, new[] { mesh }));
        var center = placed.Bounds.Center;
        Assert.All(placed.Triangles, t =>
        {
            Assert.Equal(1f, t.Normal.Length(), 4);
            var centroid = (t.A + t.B + t.C) / 3;
            Assert.True(Vector3.Dot(t.Normal, centroid - center) > 0, $"normal {t.Normal} points inward at {centroid}");
        });
    }

    [Fact]
    public void Scale_OfOneModel_LeavesTheOtherModelUnchanged()
    {
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(30, 0, 0), Scale = new Vector3(1.5f) });
        var meshes = ModelLayout.MachineMeshes(project, new[] { a, b });
        AssertClose(new Vector3(10, 10, 5), meshes[0].Bounds.Size);
        AssertClose(new Vector3(15, 15, 7.5f), meshes[1].Bounds.Size);
    }
}
