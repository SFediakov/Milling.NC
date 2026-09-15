using System.Numerics;
using Miller.Core.HeightMaps;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class HeightMapTests
{
    [Fact]
    public void Constructor_FillsEveryCell()
    {
        var map = new HeightMap(1, 2, 0.5f, 4, 3, 7f);
        Assert.Equal(12, map.CellCount);
        Assert.All(map.Z, z => Assert.Equal(7f, z));
        Assert.Equal(3f, map.MaxX);
        Assert.Equal(3.5f, map.MaxY);
    }

    [Theory]
    [InlineData(0f, 4, 3)]
    [InlineData(-1f, 4, 3)]
    [InlineData(1f, 0, 3)]
    [InlineData(1f, 4, -1)]
    public void Constructor_RejectsNonPositiveSizes(float cellSize, int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeightMap(0, 0, cellSize, width, height, 0));
    }

    [Fact]
    public void Indexer_ReadsAndWritesRowMajor()
    {
        var map = new HeightMap(0, 0, 1, 3, 2, 0);
        map[2, 1] = 5f;
        Assert.Equal(5f, map[2, 1]);
        Assert.Equal(5f, map.Z[1 * 3 + 2]);
        Assert.Throws<IndexOutOfRangeException>(() => map[3, 0]);
        Assert.Throws<IndexOutOfRangeException>(() => map[0, 2]);
        Assert.Throws<IndexOutOfRangeException>(() => map[-1, 0]);
    }

    [Fact]
    public void CellCenter_And_CellOf_RoundTrip()
    {
        var map = new HeightMap(-3.5f, 2.25f, 0.4f, 7, 5, 0);
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var center = map.CellCenter(i, j);
                Assert.Equal((i, j), map.CellOf(center.X, center.Y));
            }
        }

        Assert.Equal(new Vector2(-3.3f, 2.45f), map.CellCenter(0, 0));
    }

    [Fact]
    public void CellOf_OutsideTheGrid_IsReportedByInBounds()
    {
        var map = new HeightMap(0, 0, 1, 4, 4, 0);
        var (i, j) = map.CellOf(-0.5f, 4.5f);
        Assert.Equal((-1, 4), (i, j));
        Assert.False(map.InBounds(i, j));
        Assert.True(map.InBounds(0, 0));
        Assert.True(map.InBounds(3, 3));
        Assert.False(map.InBounds(4, 3));
    }

    [Fact]
    public void MinAndMax_IgnoreNaN()
    {
        var map = new HeightMap(0, 0, 1, 2, 2, float.NaN);
        map[0, 0] = 3f;
        map[1, 1] = -2f;
        Assert.Equal(-2f, map.Min());
        Assert.Equal(3f, map.Max());
        Assert.Equal(2, map.MaterialCellCount());
    }

    [Fact]
    public void MinAndMax_AreNaN_WhenEveryCellIsNaN()
    {
        var map = new HeightMap(0, 0, 1, 2, 2, float.NaN);
        Assert.True(float.IsNaN(map.Min()));
        Assert.True(float.IsNaN(map.Max()));
        Assert.Equal(0, map.MaterialCellCount());
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var map = new HeightMap(1, 1, 1, 2, 2, 1f);
        var clone = map.Clone();
        clone[0, 0] = 9f;
        Assert.Equal(1f, map[0, 0]);
        Assert.Equal(9f, clone[0, 0]);
        Assert.True(map.SameGridAs(clone));
        Assert.False(map.SameGridAs(new HeightMap(0, 1, 1, 2, 2, 1f)));
    }

    [Fact]
    public void Fill_OverwritesEveryCell()
    {
        var map = new HeightMap(0, 0, 1, 3, 3, 1f);
        map[1, 1] = 4f;
        map.Fill(2f);
        Assert.All(map.Z, z => Assert.Equal(2f, z));
    }

    [Fact]
    public void Bounds_CoverTheWholeGrid()
    {
        var map = new HeightMap(1, 2, 0.5f, 4, 2, 0);
        var bounds = map.Bounds(-1, 3);
        Assert.Equal(new Vector3(1, 2, -1), bounds.Min);
        Assert.Equal(new Vector3(3, 3, 3), bounds.Max);
    }
}
