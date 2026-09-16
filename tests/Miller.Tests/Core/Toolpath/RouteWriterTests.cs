using System.Numerics;
using Miller.Core.Toolpaths;
using Miller.Solver;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// The writer starts with a plunge from safe Z, follows the surface polyline with feeds, ramps
// down gentle slopes but plunges in place instead of dropping down a wall at feed rate, travels
// along the surface or over safe Z by time, and ends with a retract.
public sealed class RouteWriterTests
{
    private const float SafeZ = 10f;

    private static RouteGrid Row(params float[] floors) => new(floors, floors.Length, 1, 0, 0, 1f);

    [Fact]
    public void FirstTravel_PlungesFromSafeZ_AndFinishRetracts()
    {
        var writer = new RouteWriter(TestContexts.Parameters(), SafeZ);
        Assert.Null(writer.Position);
        writer.Travel(new RoutePoint(0.5f, 0.5f, 2f), Row(0, 0, 0));
        var path = writer.Finish();
        Assert.Equal(2, path.Count);
        Assert.Equal(new ToolpathSegment(new Vector3(0.5f, 0.5f, SafeZ), new Vector3(0.5f, 0.5f, 2f), MoveKind.Plunge, 200), path.Segments[0]);
        Assert.Equal(MoveKind.Rapid, path.Segments[1].Kind);
        Assert.Equal(SafeZ, path.Segments[1].End.Z);
    }

    [Fact]
    public void Follow_OverARidge_ClimbsAtFeed_AndDescendsAsFeedThenPlunge()
    {
        var grid = Row(0, 0, 3, 0, 0);
        var writer = new RouteWriter(TestContexts.Parameters(), SafeZ);
        writer.Travel(new RoutePoint(0.5f, 0.5f, 0f), grid);
        writer.Follow(new RoutePoint(4.5f, 0.5f, 0f), grid);
        Assert.Equal(new Vector3(4.5f, 0.5f, 0f), writer.Position);
        var segments = writer.Finish().Segments.Skip(1).SkipLast(1).ToList();
        // Flat to x = 1, ramp up to (2, 3), flat to (3, 3), flat at 3 to x = 4, plunge to 0, flat to 4.5.
        Assert.All(segments.Where(s => s.Kind == MoveKind.Feed), s => Assert.True(s.End.Z >= s.Start.Z || s.End.Z >= s.Start.Z - RouteWriter.LevelEpsilon, $"feed descends {s}"));
        var plunge = Assert.Single(segments, s => s.Kind == MoveKind.Plunge);
        Assert.Equal(new Vector3(4f, 0.5f, 3f), plunge.Start);
        Assert.Equal(new Vector3(4f, 0.5f, 0f), plunge.End);
        Assert.Contains(segments, s => s.Kind == MoveKind.Feed && s.Start.Z == 0f && s.End.Z == 3f);
    }

    [Fact]
    public void Follow_DownAGentleSlope_RampsAtFeed_ButDropsInPlaceWhenSteep()
    {
        var grid = Row(0, 0, 0);
        var ramp = new RouteWriter(TestContexts.Parameters(), SafeZ);
        ramp.Travel(new RoutePoint(0.5f, 0.5f, 2f), grid);
        ramp.Follow(new RoutePoint(2.5f, 0.5f, 0f), grid);
        var ramped = ramp.Finish().Segments.Skip(1).SkipLast(1).ToList();
        Assert.All(ramped, s => Assert.Equal(MoveKind.Feed, s.Kind));
        Assert.Equal(new Vector3(2.5f, 0.5f, 0f), ramped[^1].End);
        Assert.Equal(1.5f, ramped[0].End.Z, 4);

        var steep = new RouteWriter(TestContexts.Parameters(), SafeZ);
        steep.Travel(new RoutePoint(0.5f, 0.5f, 2f), grid);
        steep.Follow(new RoutePoint(0.7f, 0.5f, 0f), grid);
        var dropped = steep.Finish().Segments.Skip(1).SkipLast(1).ToList();
        Assert.Equal(2, dropped.Count);
        Assert.Equal(new ToolpathSegment(new Vector3(0.5f, 0.5f, 2f), new Vector3(0.7f, 0.5f, 2f), MoveKind.Feed, 800), dropped[0]);
        Assert.Equal(new ToolpathSegment(new Vector3(0.7f, 0.5f, 2f), new Vector3(0.7f, 0.5f, 0f), MoveKind.Plunge, 200), dropped[1]);
        Assert.Throws<InvalidOperationException>(() => new RouteWriter(TestContexts.Parameters(), SafeZ).Follow(new RoutePoint(0, 0, 0), grid));
    }

    [Fact]
    public void Travel_TakesTheSurfaceWhenItIsFaster_AndTheRetractOtherwise()
    {
        var parameters = TestContexts.Parameters();
        // Short flat move: along the surface at feed rate beats a 20 mm retract and plunge.
        var flat = Row(0, 0, 0, 0, 0);
        var near = new RouteWriter(parameters, SafeZ);
        near.Travel(new RoutePoint(0.5f, 0.5f, 0f), flat);
        near.Travel(new RoutePoint(3.5f, 0.5f, 0f), flat);
        Assert.DoesNotContain(near.Finish().Segments.SkipLast(1), s => s.Kind == MoveKind.Rapid);

        // A 9 mm ridge in between: climbing and plunging at feed and plunge rates costs more than the
        // retract, rapid and plunge.
        var ridge = Row(0, 0, 9, 0, 0);
        var far = new RouteWriter(parameters, SafeZ);
        far.Travel(new RoutePoint(0.5f, 0.5f, 0f), ridge);
        far.Travel(new RoutePoint(4.5f, 0.5f, 0f), ridge);
        var segments = far.Finish().Segments;
        Assert.Equal(MoveKind.Rapid, segments[1].Kind);
        Assert.Equal(SafeZ, segments[1].End.Z);
        Assert.Equal(MoveKind.Rapid, segments[2].Kind);
        Assert.Equal(MoveKind.Plunge, segments[3].Kind);
        Assert.Equal(new Vector3(4.5f, 0.5f, 0f), segments[3].End);
    }

    [Fact]
    public void Travel_AboveSafeZ_IsRejected()
    {
        var writer = new RouteWriter(TestContexts.Parameters(), SafeZ);
        Assert.Throws<ArgumentException>(() => writer.Travel(new RoutePoint(0, 0, SafeZ + 1), Row(0)));
        Assert.Throws<ArgumentException>(() => new RouteWriter(new Miller.Core.Setup.CuttingParameters { FeedRate = 0 }, SafeZ));
    }
}
