using System.Numerics;
using Miller.Core.Toolpaths;
using Miller.Solver;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// T-216 (user request): a move over cells lifted to safe Z is on safe Z from 1 mm of XY travel before the
// first lifted cell to 1 mm after the last one, rising and landing in place at the move's own start or end
// when they lie nearer, and two stretches whose widened ends meet stay on safe Z between them. Moves below
// safe Z keep the plain surface polyline. T-217 (user request): a travel times its vertical travel at the
// route solver's Z speed, three times slower than XY.
public sealed class RouteWriterSafeHeightTests
{
    private const float SafeZ = 10f;

    // One row of 1 mm cells, the tool at y = 0.5; a cell at SafeZ is one the strategy lifted over uncut stock.
    private static RouteGrid Row(params float[] floors) => new(floors, floors.Length, 1, 0, 0, 1f);

    private static List<ToolpathSegment> Follow(RouteGrid grid, float fromX, float toX)
    {
        var writer = new RouteWriter(TestContexts.Parameters(), SafeZ);
        writer.Travel(new RoutePoint(fromX, 0.5f, 0f), grid);
        writer.Follow(new RoutePoint(toX, 0.5f, 0f), grid);
        return writer.Finish().Segments.Skip(1).SkipLast(1).ToList();
    }

    private static ToolpathSegment Single(List<ToolpathSegment> segments, MoveKind kind, Func<ToolpathSegment, bool> where)
        => Assert.Single(segments, s => s.Kind == kind && where(s));

    [Fact]
    public void TheMargin_IsOneMillimetre()
    {
        Assert.Equal(1f, RouteWriter.SafeMargin);
    }

    [Fact]
    public void AMoveOverLiftedCells_ReachesSafeZOneMillimetreBefore_AndLandsOneMillimetreAfter()
    {
        // Lifted cells x in [4, 6): the tool rises at x = 3 and lands at x = 7 instead of at 4 and 6.
        var segments = Follow(Row(0, 0, 0, 0, SafeZ, SafeZ, 0, 0, 0, 0), 0.5f, 9.5f);
        var rise = Single(segments, MoveKind.Feed, s => s.End.Z == SafeZ);
        Assert.Equal(3f, rise.Start.X, 3);
        Assert.Equal(3f, rise.End.X, 3);
        Assert.Equal(0f, rise.Start.Z, 4);
        var over = Single(segments, MoveKind.Rapid, _ => true);
        Assert.Equal(new Vector3(rise.End.X, 0.5f, SafeZ), over.Start);
        Assert.Equal(7f, over.End.X, 3);
        Assert.Equal(SafeZ, over.End.Z);
        var land = Single(segments, MoveKind.Plunge, _ => true);
        Assert.Equal(over.End, land.Start);
        Assert.Equal(new Vector3(over.End.X, 0.5f, 0f), land.End);

        // Everything else runs on the floor.
        Assert.All(segments.Where(s => s != rise && s != over && s != land), s => Assert.True(s.Start.Z == 0f && s.End.Z == 0f, $"{s}"));
        Assert.Equal(new Vector3(9.5f, 0.5f, 0f), segments[^1].End);
    }

    [Fact]
    public void StartAndEndNearerThanTheMargin_RiseAndLandInPlace()
    {
        // The start lies 0.5 mm before the lifted cells, the end 0.5 mm after them.
        var segments = Follow(Row(0, 0, 0, 0, SafeZ, SafeZ, 0, 0, 0, 0), 3.5f, 6.5f);
        Assert.Equal(3, segments.Count);
        Assert.Equal(new ToolpathSegment(new Vector3(3.5f, 0.5f, 0f), new Vector3(3.5f, 0.5f, SafeZ), MoveKind.Feed, 800), segments[0]);
        Assert.Equal(new ToolpathSegment(new Vector3(3.5f, 0.5f, SafeZ), new Vector3(6.5f, 0.5f, SafeZ), MoveKind.Rapid, 3000), segments[1]);
        Assert.Equal(new ToolpathSegment(new Vector3(6.5f, 0.5f, SafeZ), new Vector3(6.5f, 0.5f, 0f), MoveKind.Plunge, 800), segments[2]);
    }

    [Fact]
    public void TwoStretchesWhoseWidenedEndsMeet_StayOnSafeZ_OthersLandBetween()
    {
        // Lifted cells [3, 4) and [6, 7): widened to [2, 5] and [5, 8], one stretch from 2 to 8.
        var joined = Follow(Row(0, 0, 0, SafeZ, 0, 0, SafeZ, 0, 0, 0), 0.5f, 9.5f);
        var over = Single(joined, MoveKind.Rapid, _ => true);
        Assert.Equal(2f, over.Start.X, 3);
        Assert.Equal(8f, over.End.X, 3);
        Assert.DoesNotContain(joined, s => s.Kind != MoveKind.Rapid && s.Start.Z == SafeZ && s.End.Z == SafeZ);

        // Lifted cells [2, 3) and [7, 8): widened to [1, 4] and [6, 9], the tool lands at 4 and rises at 6.
        var apart = Follow(Row(0, 0, SafeZ, 0, 0, 0, 0, SafeZ, 0, 0), 0.5f, 9.5f);
        var rapids = apart.Where(s => s.Kind == MoveKind.Rapid).OrderBy(s => s.Start.X).ToList();
        Assert.Equal(2, rapids.Count);
        Assert.Equal(1f, rapids[0].Start.X, 3);
        Assert.Equal(4f, rapids[0].End.X, 3);
        Assert.Equal(6f, rapids[1].Start.X, 3);
        Assert.Equal(9f, rapids[1].End.X, 3);
        Assert.Contains(apart, s => s.Kind == MoveKind.Feed && s.Start.Z == 0f && s.End.Z == 0f && s.Start.X >= 4f - 1e-3f && s.End.X <= 6f + 1e-3f);
    }

    [Fact]
    public void AMoveBelowSafeZ_KeepsThePlainSurfacePolyline()
    {
        var grid = Row(0, 0, 3, 7, 0, 0);
        var points = new List<RoutePoint>();
        SurfacePath.Trace(grid, new RoutePoint(0.5f, 0.5f, 0f), new RoutePoint(5.5f, 0.5f, 0f), points);
        var segments = Follow(grid, 0.5f, 5.5f);
        var ends = segments.Select(s => s.End).ToList();
        foreach (var p in points)
        {
            Assert.Contains(new Vector3(p.X, p.Y, p.Z), ends);
        }

        Assert.DoesNotContain(segments, s => s.Kind == MoveKind.Rapid);
    }

    [Fact]
    public void ATravel_TimesItsVerticalTravelAtTheZSpeed()
    {
        // 20 mm along the floor at 800 mm/min: 0.025 min. The retract: 10 mm up at 3000, 20 mm over at 3000,
        // 10 mm down at 800, 0.0225 min when Z ran as fast as XY (the retract won); with Z three times
        // slower 0.01 + 0.0067 + 0.0375 = 0.0542 min, so the travel stays on the floor.
        Assert.Equal(3f, RouteCost.XySpeedFactor / RouteCost.ZSpeedFactor);
        var flat = Row(new float[21]);
        var writer = new RouteWriter(TestContexts.Parameters(), SafeZ);
        writer.Travel(new RoutePoint(0.5f, 0.5f, 0f), flat);
        writer.Travel(new RoutePoint(20.5f, 0.5f, 0f), flat);
        var segments = writer.Finish().Segments.Skip(1).SkipLast(1).ToList();
        Assert.All(segments, s => Assert.Equal(MoveKind.Feed, s.Kind));
        Assert.Equal(new Vector3(20.5f, 0.5f, 0f), segments[^1].End);
    }
}
