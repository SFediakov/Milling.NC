using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Io;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class MeshRasterizerTests
{
    private const float Floor = -1f;

    [Fact]
    public void CreateGridFor_SizesByCeiling()
    {
        var bounds = new BoundingBox(new Vector3(1, 2, 0), new Vector3(11, 12, 5));
        var exact = MeshRasterizer.CreateGridFor(bounds, 0.5f, Floor);
        Assert.Equal((20, 20), (exact.Width, exact.Height));
        Assert.Equal((1f, 2f), (exact.OriginX, exact.OriginY));

        var partial = MeshRasterizer.CreateGridFor(bounds, 0.3f, Floor);
        Assert.Equal((34, 34), (partial.Width, partial.Height));

        Assert.Throws<ArgumentException>(() => MeshRasterizer.CreateGridFor(BoundingBox.Empty, 0.5f, Floor));
    }

    [Fact]
    public void Box_GivesTopHeightInsideAndFloorOutside()
    {
        var box = TestMeshes.Box(10, 10, 5).Transform(Matrix4x4.CreateTranslation(5, 5, 0));
        var grid = MeshRasterizer.CreateGridFor(new BoundingBox(Vector3.Zero, new Vector3(20, 20, 5)), 0.5f, Floor);
        MeshRasterizer.Rasterize(box, grid, Floor);

        var top = 0;
        var floor = 0;
        for (var j = 0; j < grid.Height; j++)
        {
            for (var i = 0; i < grid.Width; i++)
            {
                var c = grid.CellCenter(i, j);
                var inside = c.X > 5 && c.X < 15 && c.Y > 5 && c.Y < 15;
                if (inside)
                {
                    // Interpolated, so equal within float rounding only.
                    Assert.Equal(5f, grid[i, j], 4);
                    top++;
                }
                else
                {
                    Assert.Equal(Floor, grid[i, j]);
                    floor++;
                }
            }
        }

        Assert.Equal(400, top);
        Assert.Equal(1200, floor);
    }

    [Fact]
    public void Rasterize_ResetsPreviousContentToFloor()
    {
        var grid = new HeightMap(0, 0, 1, 4, 4, 9f);
        MeshRasterizer.Rasterize(new Mesh(Array.Empty<Triangle>()), grid, Floor);
        Assert.All(grid.Z, z => Assert.Equal(Floor, z));
    }

    [Fact]
    public void AdjacentTriangles_LeaveNoGaps()
    {
        // Two triangles share the diagonal x = y, which passes exactly through every cell center
        // (k + 0.5, k + 0.5). Both must claim those centers; a strict inside test leaves them at floor.
        var a = new Vector3(0, 0, 1);
        var b = new Vector3(8, 0, 1);
        var c = new Vector3(8, 8, 1);
        var d = new Vector3(0, 8, 1);
        var quad = new Mesh(new[] { new Triangle(a, b, c), new Triangle(a, c, d) });
        var grid = new HeightMap(0, 0, 1, 8, 8, Floor);
        MeshRasterizer.Rasterize(quad, grid, Floor);
        Assert.All(grid.Z, z => Assert.Equal(1f, z));
    }

    [Fact]
    public void Slope_IsInterpolatedAtCellCenters()
    {
        // z = x over a 4 x 4 square; the cell center at x = 2.5 must read 2.5.
        var quad = new Mesh(new[]
        {
            new Triangle(new Vector3(0, 0, 0), new Vector3(4, 0, 4), new Vector3(4, 4, 4)),
            new Triangle(new Vector3(0, 0, 0), new Vector3(4, 4, 4), new Vector3(0, 4, 0)),
        });
        var grid = new HeightMap(0, 0, 1, 4, 4, Floor);
        MeshRasterizer.Rasterize(quad, grid, Floor);
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(i + 0.5f, grid[i, j], 4);
            }
        }
    }

    [Fact]
    public void Fixture_MaxMatchesBoundsAndStaysInsideThem()
    {
        var (mesh, report) = StlReader.Read(TestMeshes.FixturePath());
        var grid = MeshRasterizer.CreateGridFor(report.Bounds, 0.2f, Floor);
        MeshRasterizer.Rasterize(mesh, grid, Floor);

        // The top of the standing heart is a ridge, so sampling at cell centers loses up to about
        // half a cell of height along a unit slope; one cell size is the bound the guide promises.
        Assert.InRange(grid.Max(), report.Bounds.Max.Z - grid.CellSize, report.Bounds.Max.Z + 1e-3f);
        var covered = grid.Z.Count(z => z > Floor);
        Assert.True(covered > 0);
        Assert.True(covered < grid.CellCount, "the heart does not fill its bounding rectangle");
        Assert.All(grid.Z.Where(z => z > Floor), z => Assert.True(z >= report.Bounds.Min.Z - 1e-3f));
    }

    [Fact]
    public void DownwardFacing_FindsTheBottomOfABoxAndOverhangsOfTheFixture()
    {
        var box = TestMeshes.Box(10, 10, 5);
        var grid = MeshRasterizer.CreateGridFor(box.Bounds, 0.5f, Floor);
        MeshRasterizer.RasterizeDownwardFacing(box, grid);
        Assert.Equal(400, grid.MaterialCellCount());
        Assert.All(grid.Z, z => Assert.Equal(0f, z));

        var (heart, report) = StlReader.Read(TestMeshes.FixturePath());
        var heartGrid = MeshRasterizer.CreateGridFor(report.Bounds, 0.2f, Floor);
        MeshRasterizer.RasterizeDownwardFacing(heart, heartGrid);
        Assert.True(heartGrid.MaterialCellCount() > 0);
        Assert.True(heartGrid.Max() > report.Bounds.Min.Z + 1f, "the standing heart has overhangs well above the floor");
    }
}
