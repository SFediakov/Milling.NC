using System.Numerics;
using Miller.Core.Toolpaths;
using Miller.Solver;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// The turn fine realized in the toolpath: every movement (a run of feeds straight on in XY) runs its
// first and last 5 mm at a third of the rate, a movement up to 10 mm wholly; rapids, plunges, feeds
// without XY travel and direction changes end a movement; the geometry stays.
public sealed class SlowZonesTests
{
    private const float Feed = 900f;
    private const float Z = -1f;
    private static readonly float Slow = SlowZones.SlowRate(Feed);

    private static Vector3 P(float x, float y) => new(x, y, Z);

    private static Toolpath Feeds(params Vector3[] points)
    {
        var path = new Toolpath();
        for (var k = 1; k < points.Length; k++)
        {
            path.Add(new ToolpathSegment(points[k - 1], points[k], MoveKind.Feed, Feed));
        }

        return path;
    }

    private static RouteProblem Problem(Vector3[] points)
        => new(new RouteGrid(new float[1], 1, 1, 0, 0, 0.2f), points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(), new float[points.Length]);

    private static float SlowLength(Toolpath path) => path.Segments.Where(s => s.FeedRate == Slow).Sum(s => s.Length);

    // The split path runs through every original vertex, joins up and keeps the length.
    private static void AssertChain(Toolpath original, Toolpath split)
    {
        Assert.Equal(original.Segments[0].Start, split.Segments[0].Start);
        Assert.Equal(original.Segments[^1].End, split.Segments[^1].End);
        for (var k = 1; k < split.Count; k++)
        {
            Assert.Equal(split.Segments[k - 1].End, split.Segments[k].Start);
        }

        Assert.Equal(original.TotalLength(), split.TotalLength(), 3);
        var ends = split.Segments.Select(s => s.End).ToHashSet();
        Assert.All(original.Segments, s => Assert.Contains(s.End, ends));
        Assert.All(split.Segments, s => Assert.True(s.Length > 0, "a zero length piece"));
    }

    [Fact]
    public void Constants_AreAThird_AndAgreeWithTheNativeLibrary()
    {
        Assert.Equal(1f / 3f, TurnFine.SlowSpeedFactor);
        Assert.Equal(TurnFine.SlowSpeedFactor, TurnFine.NativeSlowSpeedFactor);
        Assert.Equal(BitConverter.SingleToInt32Bits(TurnFine.PerSlowMillimetre), BitConverter.SingleToInt32Bits(TurnFine.NativePerSlowMillimetre));
        Assert.Equal(2f / 3f, TurnFine.PerSlowMillimetre, 6);
        Assert.Equal(300f, SlowZones.SlowRate(900f), 4);
    }

    [Fact]
    public void StraightMovement_SlowsTheFirstAndLast5Mm()
    {
        var original = Feeds(P(0, 0), P(20, 0));
        var split = SlowZones.Apply(original);
        Assert.Equal(3, split.Count);
        Assert.Equal(new[] { Slow, Feed, Slow }, split.Segments.Select(s => s.FeedRate));
        Assert.Equal(5f, split.Segments[0].End.X, 4);
        Assert.Equal(15f, split.Segments[1].End.X, 4);
        Assert.Equal(10f, SlowLength(split), 4);
        Assert.All(split.Segments, s => Assert.Equal(MoveKind.Feed, s.Kind));
        AssertChain(original, split);
    }

    [Fact]
    public void RightAngle_SlowsBothSidesOfTheCorner()
    {
        var original = Feeds(P(0, 0), P(20, 0), P(20, 20));
        var split = SlowZones.Apply(original);
        Assert.Equal(6, split.Count);
        Assert.Equal(new[] { Slow, Feed, Slow, Slow, Feed, Slow }, split.Segments.Select(s => s.FeedRate));
        Assert.Equal(P(20, 0), split.Segments[2].End);
        Assert.Equal(20f, SlowLength(split), 4);
        AssertChain(original, split);
    }

    [Theory]
    [InlineData(8f)]
    [InlineData(10f)]
    public void MovementUpToTwoZones_IsWhollySlow(float length)
    {
        var original = Feeds(P(0, 0), P(length, 0));
        var only = Assert.Single(SlowZones.Apply(original).Segments);
        Assert.Equal(Slow, only.FeedRate);
        Assert.Equal(original.Segments[0].Start, only.Start);
        Assert.Equal(original.Segments[0].End, only.End);
    }

    [Fact]
    public void CollinearJunctions_ContinueTheMovement()
    {
        // A kept vertex on the line, a jog within the straight sine, and a change of slope in the same
        // XY direction: one movement of 20 mm each, the vertex kept.
        var paths = new[]
        {
            Feeds(P(0, 0), P(10, 0), P(20, 0)),
            Feeds(P(0, 0), P(10, 0), new Vector3(20, 0.005f, Z)),
            Feeds(P(0, 0), P(10, 0), new Vector3(20, 0, Z - 1)),
        };
        foreach (var path in paths)
        {
            var split = SlowZones.Apply(path);
            Assert.Equal(4, split.Count);
            Assert.Equal(new[] { Slow, Feed, Feed, Slow }, split.Segments.Select(s => s.FeedRate));
            Assert.Equal(path.Segments[0].End, split.Segments[1].End);
            AssertChain(path, split);
        }
    }

    [Fact]
    public void VerticalFeed_Plunge_Rapid_AndReversal_EndMovements()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(P(0, 0), P(20, 0), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(P(20, 0), new Vector3(20, 0, Z - 1), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(20, 0, Z - 1), new Vector3(40, 0, Z - 1), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(40, 0, Z - 1), new Vector3(40, 0, Z - 2), MoveKind.Plunge, 200f));
        path.Add(new ToolpathSegment(new Vector3(40, 0, Z - 2), new Vector3(60, 0, Z - 2), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(60, 0, Z - 2), new Vector3(60, 0, 10), MoveKind.Rapid, 3000f));
        path.Add(new ToolpathSegment(new Vector3(60, 0, 10), new Vector3(80, 0, 10), MoveKind.Rapid, 3000f));
        path.Add(new ToolpathSegment(new Vector3(80, 0, 10), new Vector3(80, 0, Z), MoveKind.Plunge, 200f));
        path.Add(new ToolpathSegment(new Vector3(80, 0, Z), new Vector3(100, 0, Z), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(100, 0, Z), new Vector3(80, 0, Z), MoveKind.Feed, Feed));

        var split = SlowZones.Apply(path);
        // Five movements of 20 mm in three pieces each; the vertical feed, the plunges and the rapids as they were.
        Assert.Equal(5 * 3 + 5, split.Count);
        Assert.Equal(50f, SlowLength(split), 3);
        var vertical = Assert.Single(split.Segments, s => s.Kind == MoveKind.Feed && s.Start.X == s.End.X && s.Start.Y == s.End.Y);
        Assert.Equal(Feed, vertical.FeedRate);
        Assert.Equal(2, split.Segments.Count(s => s.Kind == MoveKind.Plunge));
        Assert.Equal(2, split.Segments.Count(s => s.Kind == MoveKind.Rapid));
        Assert.All(split.Segments.Where(s => s.Kind != MoveKind.Feed), s => Assert.Contains(s, path.Segments));
        AssertChain(path, split);
    }

    [Fact]
    public void SlowLength_IsTheSolverFineLength_PlusTheRouteEnds()
    {
        // Three movements of 30 mm: the solver fines the two turns (10 mm each); the toolpath also
        // slows the first and last 5 mm of the route, which every order of a route pays alike.
        var points = new[] { P(0, 0), P(30, 0), P(30, 30), P(60, 30) };
        var order = Enumerable.Range(0, points.Length).ToArray();
        Assert.Equal(20f, TurnFine.SlowLength(Problem(points), order), 3);
        Assert.Equal(20f + 2 * TurnFine.SlowZone, SlowLength(SlowZones.Apply(Feeds(points))), 3);

        // A first movement of 8 mm is wholly slow (the solver counts the 3 mm of it before the turn's
        // zone), the end zone adds 5 mm: 20 + 3 + 5.
        var shortStart = new[] { P(0, 0), P(8, 0), P(8, 38), P(40, 38) };
        Assert.Equal(20f, TurnFine.SlowLength(Problem(shortStart), order), 3);
        Assert.Equal(28f, SlowLength(SlowZones.Apply(Feeds(shortStart))), 3);
    }

    [Fact]
    public void EmptyAndRapidOnlyPaths_AreUnchanged()
    {
        Assert.Equal(0, SlowZones.Apply(new Toolpath()).Count);
        var rapids = new Toolpath();
        rapids.Add(new ToolpathSegment(new Vector3(0, 0, 10), new Vector3(50, 0, 10), MoveKind.Rapid, 3000f));
        Assert.Equal(rapids.Segments, SlowZones.Apply(rapids).Segments);
    }
}
