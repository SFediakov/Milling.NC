using System.Numerics;
using Miller.Core.Geometry;
using Xunit;

namespace Miller.Tests.Core.Geometry;

public sealed class BoundingBoxTests
{
    private static readonly BoundingBox Unit = new(Vector3.Zero, Vector3.One);

    [Fact]
    public void Empty_IsEmpty_AndHasZeroSizeAndCenter()
    {
        Assert.True(BoundingBox.Empty.IsEmpty);
        Assert.Equal(Vector3.Zero, BoundingBox.Empty.Size);
        Assert.Equal(Vector3.Zero, BoundingBox.Empty.Center);
        Assert.False(Unit.IsEmpty);
    }

    [Fact]
    public void SizeAndCenter_FromMinMax()
    {
        var box = new BoundingBox(new Vector3(-1, 0, 3), new Vector3(1, 2, 5));
        Assert.Equal(new Vector3(2, 2, 2), box.Size);
        Assert.Equal(new Vector3(0, 1, 4), box.Center);
    }

    [Fact]
    public void Include_ExpandsFromEmpty()
    {
        var box = BoundingBox.Empty.Include(new Vector3(1, 2, 3)).Include(new Vector3(-1, 0, 5));
        Assert.Equal(new Vector3(-1, 0, 3), box.Min);
        Assert.Equal(new Vector3(1, 2, 5), box.Max);
    }

    [Fact]
    public void Include_PointInside_DoesNotChange()
    {
        Assert.Equal(Unit, Unit.Include(new Vector3(0.5f)));
    }

    [Fact]
    public void Union_EmptyIsIdentity()
    {
        Assert.Equal(Unit, BoundingBox.Empty.Union(Unit));
        Assert.Equal(Unit, Unit.Union(BoundingBox.Empty));
        Assert.True(BoundingBox.Empty.Union(BoundingBox.Empty).IsEmpty);
    }

    [Fact]
    public void Union_CoversBothBoxes()
    {
        var other = new BoundingBox(new Vector3(2, -1, 0.5f), new Vector3(3, 0.5f, 0.75f));
        var union = Unit.Union(other);
        Assert.Equal(new Vector3(0, -1, 0), union.Min);
        Assert.Equal(new Vector3(3, 1, 1), union.Max);
    }

    [Theory]
    [InlineData(0.5f, 0.5f, 0.5f, true)]
    [InlineData(0f, 0f, 0f, true)]
    [InlineData(1f, 1f, 1f, true)]
    [InlineData(1.0001f, 0.5f, 0.5f, false)]
    [InlineData(0.5f, -0.0001f, 0.5f, false)]
    public void Contains_Point_IsInclusive(float x, float y, float z, bool expected)
    {
        Assert.Equal(expected, Unit.Contains(new Vector3(x, y, z)));
    }

    [Fact]
    public void Contains_Box()
    {
        Assert.True(Unit.Contains(new BoundingBox(new Vector3(0.25f), new Vector3(0.75f))));
        Assert.True(Unit.Contains(Unit));
        Assert.False(Unit.Contains(new BoundingBox(new Vector3(0.25f), new Vector3(1.25f, 0.5f, 0.5f))));
        Assert.True(Unit.Contains(BoundingBox.Empty));
        Assert.False(BoundingBox.Empty.Contains(Unit));
    }
}
