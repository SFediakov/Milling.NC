using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

public sealed class AxisSetupTests
{
    private const float Tolerance = 1e-4f;

    // Stock of 20 x 20 x 10 around the 10 x 10 x 5 box: auto-fit centers the box in XY and puts
    // the box top at the stock top, so the box occupies [5,15] x [5,15] x [5,10] of the stock.
    private static StockDefinition FitStock() => new()
    {
        Shape = StockShape.Box,
        SizeX = 20,
        SizeY = 20,
        SizeZ = 10,
        Placement = StockPlacement.AutoFitWithMargin,
    };

    // Explicit placement with the stock corner on the model corner and zero at the corner: the
    // transform is then the pure orientation.
    private static StockDefinition CornerStock() => new()
    {
        Shape = StockShape.Box,
        SizeX = 100,
        SizeY = 100,
        SizeZ = 100,
        Placement = StockPlacement.Explicit,
        ExplicitOrigin = Vector3.Zero,
    };

    [Fact]
    public void Default_IsIdentityMappingWithoutFlipsOrRotation()
    {
        var setup = AxisSetup.Default();
        Assert.Equal(ModelAxis.X, setup.MapX);
        Assert.Equal(ModelAxis.Y, setup.MapY);
        Assert.Equal(ModelAxis.Z, setup.MapZ);
        Assert.False(setup.FlipX || setup.FlipY || setup.FlipZ);
        Assert.Equal(0f, setup.RotationX + setup.RotationY + setup.RotationZ);
        Assert.Equal(OriginMode.StockCornerMinXYMinZ, setup.OriginMode);
        Assert.Equal(Matrix4x4.Identity, setup.ToOrientationMatrix());
    }

    [Fact]
    public void Identity_LeavesTheCubeInPlace()
    {
        var cube = TestMeshes.UnitCube();
        var matrix = AxisSetup.Default().ToMatrix(cube.Bounds, CornerStock());
        var moved = cube.Transform(matrix);

        Assert.Equal(Matrix4x4.Identity, matrix);
        Assert.Equal(cube.Bounds, moved.Bounds);
        Assert.True(TestMeshes.IsClosed(moved));
    }

    [Fact]
    public void SwappingYAndZ_TurnsTheBoxInto10x5x10()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var setup = new AxisSetup { MapY = ModelAxis.Z, MapZ = ModelAxis.Y };
        var moved = box.Transform(setup.ToMatrix(box.Bounds, CornerStock()));

        AssertClose(new Vector3(10, 5, 10), moved.Bounds.Size);
        AssertClose(Vector3.Zero, moved.Bounds.Min);
        Assert.True(TestMeshes.IsClosed(moved));
    }

    [Fact]
    public void Mapping_IsAPermutationOfComponents()
    {
        // machine X = model Y, machine Y = model Z, machine Z = model X
        var setup = new AxisSetup { MapX = ModelAxis.Y, MapY = ModelAxis.Z, MapZ = ModelAxis.X };
        var machine = Vector3.Transform(new Vector3(1, 2, 3), setup.ToOrientationMatrix());
        AssertClose(new Vector3(2, 3, 1), machine);
    }

    [Fact]
    public void DuplicateMapping_Throws()
    {
        var setup = new AxisSetup { MapX = ModelAxis.Y, MapY = ModelAxis.Y };
        var ex = Assert.Throws<ArgumentException>(() => setup.ToOrientationMatrix());
        Assert.Contains("Y", ex.Message);
    }

    [Fact]
    public void FlipZ_MirrorsAndKeepsOutwardNormals()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var setup = new AxisSetup { FlipZ = true };
        var orientation = setup.ToOrientationMatrix();
        Assert.True(orientation.GetDeterminant() < 0);

        var mirrored = box.Transform(orientation);
        AssertClose(new Vector3(0, 0, -5), mirrored.Bounds.Min);
        AssertClose(new Vector3(10, 10, 0), mirrored.Bounds.Max);
        Assert.True(TestMeshes.IsClosed(mirrored));
        var topFaces = mirrored.Triangles.Where(t => t.MinZ > -Tolerance).ToList();
        Assert.Equal(2, topFaces.Count);
        Assert.All(topFaces, t => AssertClose(Vector3.UnitZ, t.Normal));

        // With the corner stock the mirrored box sits at z in [0, 5] again, on the stock bottom.
        var placed = box.Transform(setup.ToMatrix(box.Bounds, CornerStock()));
        AssertClose(Vector3.Zero, placed.Bounds.Min);
        AssertClose(new Vector3(10, 10, 5), placed.Bounds.Max);
    }

    [Fact]
    public void RotationZ90_MapsXToY()
    {
        var setup = new AxisSetup { RotationZ = 90 };
        var rotated = Vector3.Transform(Vector3.UnitX, setup.ToOrientationMatrix());
        AssertClose(Vector3.UnitY, rotated);
    }

    [Fact]
    public void RotationOrder_IsXThenYThenZ()
    {
        // Rotating (0,0,1) by X 90 gives (0,-1,0); then Z 90 turns that into (1,0,0).
        // In the other order (Z first, then X) the result would be (0,-1,0) -> Z 90 has no effect on
        // the Z axis, then X 90 gives (0,-1,0).
        var setup = new AxisSetup { RotationX = 90, RotationZ = 90 };
        var result = Vector3.Transform(Vector3.UnitZ, setup.ToOrientationMatrix());
        AssertClose(Vector3.UnitX, result);
    }

    [Fact]
    public void Rotation_PreservesNormalsAndClosure()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var setup = new AxisSetup { RotationX = 90 };
        var rotated = box.Transform(setup.ToOrientationMatrix());
        Assert.True(TestMeshes.IsClosed(rotated));
        // The former top (+Z) now faces -Y.
        var formerTop = rotated.Triangles.Where(t => t.A.Y < -5 + Tolerance && t.B.Y < -5 + Tolerance && t.C.Y < -5 + Tolerance).ToList();
        Assert.Equal(2, formerTop.Count);
        Assert.All(formerTop, t => AssertClose(-Vector3.UnitY, t.Normal));
    }

    [Theory]
    [InlineData(OriginMode.StockCornerMinXYMinZ, 5, 5, 5, 15, 15, 10)]
    [InlineData(OriginMode.StockCornerMinXYTopZ, 5, 5, -5, 15, 15, 0)]
    [InlineData(OriginMode.StockCenterTopZ, -5, -5, -5, 5, 5, 0)]
    [InlineData(OriginMode.Custom, 4, 3, 2, 14, 13, 7)]
    public void OriginMode_PlacesTheExpectedStockPointAtZero(
        OriginMode mode, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        var box = TestMeshes.Box(10, 10, 5);
        var setup = new AxisSetup { OriginMode = mode, CustomOffset = new Vector3(1, 2, 3) };
        var stock = FitStock();
        var placed = box.Transform(setup.ToMatrix(box.Bounds, stock));

        AssertClose(new Vector3(minX, minY, minZ), placed.Bounds.Min);
        AssertClose(new Vector3(maxX, maxY, maxZ), placed.Bounds.Max);

        // The stock corner recomputed from the machine-space bounds is at minus the origin offset.
        var corner = AxisSetup.StockCorner(placed.Bounds, stock);
        AssertClose(-setup.OriginOffset(stock), corner);
    }

    [Fact]
    public void ExplicitPlacement_OffsetsTheStockFromTheModelCorner()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var stock = new StockDefinition
        {
            SizeX = 30,
            SizeY = 30,
            SizeZ = 8,
            Placement = StockPlacement.Explicit,
            ExplicitOrigin = new Vector3(-10, -10, -3),
        };
        var setup = new AxisSetup { OriginMode = OriginMode.StockCornerMinXYMinZ };
        var placed = box.Transform(setup.ToMatrix(box.Bounds, stock));

        AssertClose(new Vector3(10, 10, 3), placed.Bounds.Min);
        AssertClose(new Vector3(20, 20, 8), placed.Bounds.Max);
        AssertClose(Vector3.Zero, AxisSetup.StockCorner(placed.Bounds, stock));
    }

    [Fact]
    public void Cylinder_UsesItsBoundingBoxForPlacement()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var stock = new StockDefinition { Shape = StockShape.Cylinder, Diameter = 40, Height = 20 };
        var setup = new AxisSetup { OriginMode = OriginMode.StockCenterTopZ };
        var placed = box.Transform(setup.ToMatrix(box.Bounds, stock));

        AssertClose(new Vector3(40, 40, 20), AxisSetup.StockBoundingSize(stock));
        AssertClose(new Vector3(-5, -5, -5), placed.Bounds.Min);
        AssertClose(new Vector3(5, 5, 0), placed.Bounds.Max);
    }

    [Fact]
    public void TransformBounds_CoversRotatedCorners()
    {
        var bounds = new BoundingBox(Vector3.Zero, new Vector3(10, 10, 5));
        var rotated = AxisSetup.TransformBounds(bounds, Matrix4x4.CreateRotationZ(MathF.PI / 2));
        AssertClose(new Vector3(-10, 0, 0), rotated.Min);
        AssertClose(new Vector3(0, 10, 5), rotated.Max);
        Assert.True(AxisSetup.TransformBounds(BoundingBox.Empty, Matrix4x4.Identity).IsEmpty);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, actual {actual}");
    }
}
