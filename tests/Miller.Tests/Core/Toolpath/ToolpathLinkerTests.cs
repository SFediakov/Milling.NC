using System.Numerics;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class ToolpathLinkerTests
{
    private const float SafeZ = 10f;
    private static readonly CuttingParameters Parameters = new() { CellSize = 0.5f, FeedRate = 800, PlungeRate = 200, RapidRate = 3000 };

    private static Toolpath Pass(params Vector3[] points)
    {
        var pass = new Toolpath();
        for (var k = 1; k < points.Length; k++)
        {
            pass.Add(new ToolpathSegment(points[k - 1], points[k], MoveKind.Feed, Parameters.FeedRate));
        }

        return pass;
    }

    [Fact]
    public void DisjointPasses_GetRetractRapidPlungeBetweenThem()
    {
        var a = Pass(new Vector3(0, 0, -1), new Vector3(10, 0, -1));
        var b = Pass(new Vector3(20, 5, -1), new Vector3(30, 5, -1));
        var linked = ToolpathLinker.Link(new[] { a, b }, Parameters, SafeZ);

        Assert.Equal(new[]
        {
            MoveKind.Plunge, MoveKind.Feed, MoveKind.Rapid, MoveKind.Rapid, MoveKind.Plunge, MoveKind.Feed, MoveKind.Rapid,
        }, linked.Segments.Select(s => s.Kind));

        Assert.Equal(new Vector3(0, 0, SafeZ), linked.Segments[0].Start);
        Assert.Equal(new Vector3(10, 0, SafeZ), linked.Segments[2].End);
        Assert.Equal(new Vector3(20, 5, SafeZ), linked.Segments[3].End);
        Assert.Equal(new Vector3(20, 5, -1), linked.Segments[4].End);
        Assert.Equal(new Vector3(30, 5, SafeZ), linked.Segments[6].End);
        Assert.Equal(Parameters.PlungeRate, linked.Segments[0].FeedRate);
        Assert.Equal(Parameters.RapidRate, linked.Segments[2].FeedRate);
    }

    [Fact]
    public void AdjacentPasses_AreJoinedByOneFeed()
    {
        var a = Pass(new Vector3(0, 0, -1), new Vector3(10, 0, -1));
        var b = Pass(new Vector3(10, 0.4f, -1), new Vector3(0, 0.4f, -1));
        var linked = ToolpathLinker.Link(new[] { a, b }, Parameters, SafeZ);
        Assert.Equal(new[] { MoveKind.Plunge, MoveKind.Feed, MoveKind.Feed, MoveKind.Feed, MoveKind.Rapid }, linked.Segments.Select(s => s.Kind));
        Assert.Equal(new Vector3(10, 0, -1), linked.Segments[2].Start);
        Assert.Equal(new Vector3(10, 0.4f, -1), linked.Segments[2].End);
    }

    [Fact]
    public void PassesStartingWhereThePreviousEnded_NeedNoJoinSegment()
    {
        var a = Pass(new Vector3(0, 0, -1), new Vector3(10, 0, -1));
        var b = Pass(new Vector3(10, 0, -1), new Vector3(10, 10, -1));
        var linked = ToolpathLinker.Link(new[] { a, b }, Parameters, SafeZ);
        Assert.Equal(4, linked.Count);
    }

    [Fact]
    public void EmptyPasses_AreSkipped_AndNoPassGivesAnEmptyToolpath()
    {
        Assert.Equal(0, ToolpathLinker.Link(new[] { new Toolpath() }, Parameters, SafeZ).Count);
        var a = Pass(new Vector3(0, 0, -1), new Vector3(10, 0, -1));
        var linked = ToolpathLinker.Link(new[] { new Toolpath(), a, new Toolpath() }, Parameters, SafeZ);
        Assert.Equal(3, linked.Count);
    }

    [Fact]
    public void PointsAboveSafeZ_AreRejected()
    {
        var high = Pass(new Vector3(0, 0, 11), new Vector3(10, 0, 11));
        Assert.Throws<ArgumentException>(() => ToolpathLinker.Link(new[] { high }, Parameters, SafeZ));
    }
}
