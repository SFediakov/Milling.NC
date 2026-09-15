using System.Numerics;
using Miller.Core.Geometry;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Geometry;

public sealed class MeshTests
{
    private const float Tolerance = 1e-5f;

    public static TheoryData<string, Mesh> Builders => new()
    {
        { "UnitCube", TestMeshes.UnitCube() },
        { "Box", TestMeshes.Box(10, 10, 5) },
        { "Spike", TestMeshes.Spike() },
        { "SlottedPlate", TestMeshes.SlottedPlate() },
        { "BumpPlate", TestMeshes.BumpPlate() },
    };

    [Fact]
    public void UnitCube_HasTwelveTrianglesAndUnitBounds()
    {
        var cube = TestMeshes.UnitCube();
        Assert.Equal(12, cube.TriangleCount);
        Assert.Equal(new BoundingBox(Vector3.Zero, Vector3.One), cube.Bounds);
        Assert.Equal(18, TestMeshes.EdgeCount(cube));
    }

    [Fact]
    public void EmptyMesh_HasEmptyBounds()
    {
        var mesh = new Mesh(Array.Empty<Triangle>());
        Assert.Equal(0, mesh.TriangleCount);
        Assert.True(mesh.Bounds.IsEmpty);
    }

    [Fact]
    public void Transform_Translation_ShiftsBounds()
    {
        var moved = TestMeshes.Box(10, 10, 5).Transform(Matrix4x4.CreateTranslation(1, 2, 3));
        Assert.Equal(new Vector3(1, 2, 3), moved.Bounds.Min);
        Assert.Equal(new Vector3(11, 12, 8), moved.Bounds.Max);
        Assert.Equal(12, moved.TriangleCount);
    }

    [Fact]
    public void Transform_RotationZ90_SwapsXAndY()
    {
        var rotated = TestMeshes.Box(10, 10, 5).Transform(Matrix4x4.CreateRotationZ(MathF.PI / 2));
        AssertClose(new Vector3(-10, 0, 0), rotated.Bounds.Min);
        AssertClose(new Vector3(0, 10, 5), rotated.Bounds.Max);
    }

    [Fact]
    public void Transform_RecomputesNormals()
    {
        var rotated = TestMeshes.Box(10, 10, 5).Transform(Matrix4x4.CreateRotationX(MathF.PI / 2));
        // The former +Z top face now faces -Y; rotation keeps outward orientation.
        var topFace = rotated.Triangles.Where(t => t.A.Y < -4.9f && t.B.Y < -4.9f && t.C.Y < -4.9f).ToList();
        Assert.Equal(2, topFace.Count);
        Assert.All(topFace, t => AssertClose(new Vector3(0, -1, 0), t.Normal));
        Assert.True(TestMeshes.IsClosed(rotated));
    }

    [Fact]
    public void RemoveDegenerate_RemovesOnlyDegenerateTriangles()
    {
        var withDegenerate = TestMeshes.UnitCube().Triangles
            .Append(new Triangle(Vector3.Zero, Vector3.One, new Vector3(2)))
            .Append(new Triangle(Vector3.Zero, Vector3.Zero, Vector3.UnitX));
        var mesh = new Mesh(withDegenerate);
        Assert.Equal(14, mesh.TriangleCount);
        Assert.Equal(2, mesh.Triangles.Count(t => t.IsDegenerate));

        var cleaned = mesh.RemoveDegenerate();
        Assert.Equal(12, cleaned.TriangleCount);
        Assert.DoesNotContain(cleaned.Triangles, t => t.IsDegenerate);
        Assert.Equal(14, mesh.TriangleCount);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Builders_ReturnClosedMeshes(string name, Mesh mesh)
    {
        Assert.True(TestMeshes.IsClosed(mesh), $"{name} is not closed");
        Assert.DoesNotContain(mesh.Triangles, t => t.IsDegenerate);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void Builders_HaveOutwardNormals(string name, Mesh mesh)
    {
        var top = mesh.Bounds.Max.Z;
        var bottom = mesh.Bounds.Min.Z;
        var topFaces = mesh.Triangles.Where(t => t.MinZ >= top - Tolerance).ToList();
        var bottomFaces = mesh.Triangles.Where(t => t.MaxZ <= bottom + Tolerance).ToList();
        Assert.NotEmpty(bottomFaces);
        Assert.All(bottomFaces, t => Assert.True(t.Normal.Z < 0, $"{name}: bottom face normal {t.Normal}"));
        if (topFaces.Count > 0)
        {
            Assert.All(topFaces, t => Assert.True(t.Normal.Z > 0, $"{name}: top face normal {t.Normal}"));
        }
    }

    [Fact]
    public void SlottedPlate_HasTheSpecifiedSlot()
    {
        var plate = TestMeshes.SlottedPlate();
        Assert.Equal(new Vector3(20, 20, 12), plate.Bounds.Max);
        var floor = plate.Triangles.Where(t => t.Normal.Z > 0.99f && t.MaxZ < 12 - Tolerance).ToList();
        Assert.Equal(2, floor.Count);
        Assert.All(floor, t => Assert.Equal(2f, t.MinZ));
        Assert.All(floor, t => Assert.Equal(2f, t.MaxZ));
        var floorBounds = floor.Aggregate(BoundingBox.Empty, (b, t) => b.Union(t.Bounds));
        Assert.Equal(8f, floorBounds.Min.X);
        Assert.Equal(12f, floorBounds.Max.X);
    }

    [Fact]
    public void BumpPlate_HasHemisphereOnTop()
    {
        var plate = TestMeshes.BumpPlate();
        Assert.Equal(new Vector3(20, 20, 10), plate.Bounds.Max);
        Assert.Equal(Vector3.Zero, plate.Bounds.Min);
    }

    [Fact]
    public void Spike_IsAPyramid()
    {
        var spike = TestMeshes.Spike();
        Assert.Equal(6, spike.TriangleCount);
        Assert.Equal(new Vector3(10, 10, 10), spike.Bounds.Max);
    }

    [Fact]
    public void AsciiCubeText_HasTwelveFacets()
    {
        var text = TestMeshes.AsciiCubeText();
        Assert.StartsWith("solid cube", text);
        Assert.Equal(12, text.Split("facet normal").Length - 1);
        Assert.Equal(36, text.Split("vertex ").Length - 1);
        Assert.DoesNotContain(',', text);
    }

    [Fact]
    public void FixturePath_Exists()
    {
        var path = TestMeshes.FixturePath();
        Assert.True(File.Exists(path), path);
        Assert.Equal(202584, new FileInfo(path).Length);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, actual {actual}");
    }
}
