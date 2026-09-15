using System.Numerics;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class ToolpathStatisticsTests
{
    private static readonly CuttingParameters Parameters = new() { FeedRate = 800, PlungeRate = 200, RapidRate = 3000 };

    private static Toolpath ThreeSegments()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(30, 40, 5), MoveKind.Rapid, Parameters.RapidRate)); // 50
        path.Add(new ToolpathSegment(new Vector3(30, 40, 5), new Vector3(30, 40, -5), MoveKind.Plunge, Parameters.PlungeRate)); // 10
        path.Add(new ToolpathSegment(new Vector3(30, 40, -5), new Vector3(110, 40, -5), MoveKind.Feed, Parameters.FeedRate)); // 80
        return path;
    }

    [Fact]
    public void Lengths_AreSummedPerKind()
    {
        var stats = ToolpathStatistics.Compute(ThreeSegments(), Parameters);
        Assert.Equal(50f, stats.RapidLength, 4);
        Assert.Equal(10f, stats.PlungeLength, 4);
        Assert.Equal(80f, stats.FeedLength, 4);
        Assert.Equal(140f, stats.TotalLength, 4);
        Assert.Equal(3, stats.SegmentCount);
    }

    [Fact]
    public void EstimatedMinutes_IsSumOfLengthOverRate()
    {
        var stats = ToolpathStatistics.Compute(ThreeSegments(), Parameters);
        Assert.Equal(50f / 3000 + 10f / 200 + 80f / 800, stats.EstimatedMinutes, 5);
    }

    [Fact]
    public void EmptyToolpath_GivesZeros()
    {
        var stats = ToolpathStatistics.Compute(new Toolpath(), Parameters);
        Assert.Equal(new ToolpathStatistics(0, 0, 0, 0, 0, 0), stats);
    }

    [Fact]
    public void Toolpath_TracksBoundsAndLengths()
    {
        var path = ThreeSegments();
        Assert.Equal(new Vector3(0, 0, -5), path.Bounds.Min);
        Assert.Equal(new Vector3(110, 40, 5), path.Bounds.Max);
        Assert.Equal(140f, path.TotalLength(), 4);
        Assert.Equal(80f, path.TotalLength(MoveKind.Feed), 4);
        Assert.True(path.Bounds.IsEmpty == false);
        Assert.True(new Toolpath().Bounds.IsEmpty);
    }

    [Fact]
    public void Segment_ExposesLengthAndDirection()
    {
        var s = new ToolpathSegment(new Vector3(1, 1, 1), new Vector3(4, 5, 1), MoveKind.Feed, 800);
        Assert.Equal(5f, s.Length, 4);
        Assert.Equal(new Vector3(0.6f, 0.8f, 0), s.Direction);
        Assert.Equal(Vector3.Zero, new ToolpathSegment(Vector3.One, Vector3.One, MoveKind.Feed, 800).Direction);
    }

    [Fact]
    public void NonPositiveRates_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new Toolpath().Add(new ToolpathSegment(Vector3.Zero, Vector3.One, MoveKind.Feed, 0)));
        Assert.Throws<ArgumentException>(() => ToolpathStatistics.Compute(new Toolpath(), new CuttingParameters { RapidRate = 0 }));
    }
}
