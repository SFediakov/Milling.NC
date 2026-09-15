using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class MarchingSquaresTests
{
    private const float CellSize = 0.5f;
    private const int Size = 60;

    private static HeightMap Cone(params (float X, float Y)[] apexes)
    {
        var map = new HeightMap(0, 0, CellSize, Size, Size, 0f);
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                var c = map.CellCenter(i, j);
                var z = 0f;
                foreach (var (x, y) in apexes)
                {
                    z = MathF.Max(z, 10f - Vector2.Distance(c, new Vector2(x, y)));
                }

                map[i, j] = z;
            }
        }

        return map;
    }

    // Marching squares cuts every convex right-angle corner diagonally: two half edges become one diagonal.
    private static float CornerCut(float cellSize) => cellSize - cellSize * MathF.Sqrt(2) / 2;

    private static float Perimeter(IReadOnlyList<Vector2> loop)
    {
        var length = 0f;
        for (var k = 1; k < loop.Count; k++)
        {
            length += Vector2.Distance(loop[k - 1], loop[k]);
        }

        return length;
    }

    [Fact]
    public void CircularHill_GivesOneClosedLoopWithTheCirclePerimeter()
    {
        var loops = MarchingSquares.Contours(Cone((15f, 15f)), 5f);
        var loop = Assert.Single(loops);
        Assert.Equal(loop[0], loop[^1]);
        Assert.InRange(Perimeter(loop), 2 * MathF.PI * 5 * 0.95f, 2 * MathF.PI * 5 * 1.05f);
        Assert.All(loop, p => Assert.InRange(Vector2.Distance(p, new Vector2(15, 15)), 4.7f, 5.3f));
    }

    [Fact]
    public void TwoHills_GiveTwoLoops_AndALevelAboveTheMaximumGivesNone()
    {
        Assert.Equal(2, MarchingSquares.Contours(Cone((8f, 8f), (22f, 22f)), 5f).Count);
        Assert.Empty(MarchingSquares.Contours(Cone((15f, 15f)), 10.5f));
    }

    [Fact]
    public void MaterialTouchingTheBorder_ClosesAlongTheGridEdge()
    {
        var map = new HeightMap(0, 0, CellSize, 10, 6, 1f);
        var loop = Assert.Single(MarchingSquares.Contours(map, 0.5f));
        Assert.Equal(2 * (10 + 6) * CellSize - 4 * CornerCut(CellSize), Perimeter(loop), 3);
        Assert.All(loop, p =>
        {
            Assert.InRange(p.X, 0f, 10 * CellSize);
            Assert.InRange(p.Y, 0f, 6 * CellSize);
        });
    }

    [Fact]
    public void NaNRing_ClosesTheLoopAtTheRing()
    {
        var map = new HeightMap(0, 0, CellSize, 12, 12, 1f);
        for (var k = 0; k < 12; k++)
        {
            map[k, 0] = map[k, 11] = map[0, k] = map[11, k] = float.NaN;
        }

        var loop = Assert.Single(MarchingSquares.Contours(map, 0.5f));
        Assert.Equal(4 * 10 * CellSize - 4 * CornerCut(CellSize), Perimeter(loop), 3);
    }

    [Fact]
    public void MaskContours_StayInsideAllowedCells()
    {
        var grid = new HeightMap(0, 0, CellSize, 8, 8, 0f);
        var mask = new bool[8, 8];
        mask[3, 4] = true;
        var loop = Assert.Single(MarchingSquares.MaskContours(mask, grid));
        Assert.Equal(5, loop.Count);
        Assert.All(loop, p => Assert.Equal((3, 4), grid.CellOf(p.X, p.Y)));

        for (var j = 2; j < 6; j++)
        {
            for (var i = 1; i < 7; i++)
            {
                mask[i, j] = true;
            }
        }

        mask[4, 4] = false;
        var loops = MarchingSquares.MaskContours(mask, grid);
        Assert.Equal(2, loops.Count);
        Assert.All(loops.SelectMany(l => l), p =>
        {
            var (i, j) = grid.CellOf(p.X, p.Y);
            Assert.True(mask[i, j], $"point {p} lies in forbidden cell ({i},{j})");
        });
    }

    [Fact]
    public void MaskContours_SplitSaddlesSoNoSegmentCrossesAForbiddenCell()
    {
        var grid = new HeightMap(0, 0, 1f, 4, 4, 0f);
        var mask = new bool[4, 4];
        mask[1, 1] = true;
        mask[2, 2] = true;
        var loops = MarchingSquares.MaskContours(mask, grid);
        Assert.Equal(2, loops.Count);
        foreach (var loop in loops)
        {
            for (var k = 1; k < loop.Count; k++)
            {
                var mid = (loop[k - 1] + loop[k]) / 2;
                var (i, j) = grid.CellOf(mid.X, mid.Y);
                Assert.True(mask[i, j], $"segment midpoint {mid} lies in forbidden cell ({i},{j})");
            }
        }
    }
}
