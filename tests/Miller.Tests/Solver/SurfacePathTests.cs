using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// The surface polyline never dips under a plateau, lifts every edge crossing to the higher side,
// rises and descends in place at the ends, and its climb is what the route cost charges for Z.
public sealed class SurfacePathTests
{
    private const float Cell = 1f;

    // One row of five cells along X with the given floors.
    private static RouteGrid Row(params float[] floors) => new(floors, floors.Length, 1, 0, 0, Cell);

    private static List<RoutePoint> Points(RouteGrid grid, RoutePoint a, RoutePoint b, out float climb)
    {
        var points = new List<RoutePoint>();
        climb = SurfacePath.Trace(grid, a, b, points);
        return points;
    }

    [Fact]
    public void FlatRow_IsOneStraightMove()
    {
        var grid = Row(0, 0, 0, 0, 0);
        var a = new RoutePoint(0.5f, 0.5f, 0);
        var b = new RoutePoint(4.5f, 0.5f, 0);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(0f, climb);
        Assert.Equal(b, points[^1]);
        Assert.All(points, p => Assert.Equal(0f, p.Z));
        Assert.Equal(5, points.Count);
    }

    [Fact]
    public void Ridge_IsCrossedAtItsHeight_AndChargedTwice()
    {
        var grid = Row(0, 0, 5, 0, 0);
        var a = new RoutePoint(0.5f, 0.5f, 0);
        var b = new RoutePoint(4.5f, 0.5f, 0);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(10f, climb);
        Assert.Equal(new RoutePoint(1, 0.5f, 0), points[0]);
        Assert.Equal(new RoutePoint(2, 0.5f, 5), points[1]);
        Assert.Equal(new RoutePoint(3, 0.5f, 5), points[2]);
        Assert.Equal(new RoutePoint(4, 0.5f, 0), points[3]);
        Assert.Equal(b, points[4]);
        Assert.Equal(RouteCost.Planar(a, b) / RouteCost.XySpeedFactor + 10f / RouteCost.ZSpeedFactor, RouteCost.Exact(grid, a, b), 4);
    }

    [Fact]
    public void StartUnderItsPlateau_RisesInPlaceFirst()
    {
        var grid = Row(2, 0, 0, 0, 0);
        var a = new RoutePoint(0.5f, 0.5f, -1);
        var b = new RoutePoint(2.5f, 0.5f, 0);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(new RoutePoint(0.5f, 0.5f, 2), points[0]);
        Assert.Equal(new RoutePoint(1, 0.5f, 2), points[1]);
        Assert.Equal(b, points[^1]);
        Assert.Equal(3 + 2, climb);
    }

    [Fact]
    public void EndUnderItsPlateau_DescendsInPlaceLast()
    {
        var grid = Row(0, 0, 3, 0, 0);
        var a = new RoutePoint(0.5f, 0.5f, 0);
        var b = new RoutePoint(2.5f, 0.5f, -2);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(new RoutePoint(2.5f, 0.5f, 3), points[^2]);
        Assert.Equal(b, points[^1]);
        Assert.Equal(3 + 5, climb);
    }

    [Fact]
    public void NaNCells_DoNotConstrain_AndTheLineInterpolatesZ()
    {
        var grid = Row(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
        var a = new RoutePoint(0.5f, 0.5f, 0);
        var b = new RoutePoint(4.5f, 0.5f, 4);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(4f, climb, 4);
        Assert.Equal(0.5f, points[0].Z, 4);
        Assert.Equal(3.5f, points[3].Z, 4);
        Assert.Equal(b, points[^1]);
    }

    [Fact]
    public void Diagonal_ThroughGridCorners_EndsAtB()
    {
        var floors = new float[16];
        floors[5] = 7;
        var grid = new RouteGrid(floors, 4, 4, 0, 0, Cell);
        var a = new RoutePoint(0.5f, 0.5f, 0);
        var b = new RoutePoint(3.5f, 3.5f, 0);
        var points = Points(grid, a, b, out var climb);
        Assert.Equal(b, points[^1]);
        Assert.Equal(14f, climb);
        Assert.Contains(points, p => p.Z == 7);
        Assert.True(points.Count <= 10);
    }

    [Fact]
    public void SamePoint_UnderPlateau_RisesAndDescends()
    {
        var grid = Row(4, 0, 0, 0, 0);
        var a = new RoutePoint(0.5f, 0.5f, 1);
        var points = Points(grid, a, a, out var climb);
        Assert.Equal(6f, climb);
        Assert.Equal(a, points[^1]);
    }

    [Fact]
    public void LowerBound_NeverExceedsExact()
    {
        var floors = new float[400];
        for (var k = 0; k < floors.Length; k++)
        {
            floors[k] = (k * 37) % 11 - 5;
        }

        var grid = new RouteGrid(floors, 20, 20, -3, 2, 0.5f);
        for (var k = 0; k < 200; k++)
        {
            var a = new RoutePoint(-3 + (k * 7) % 100 / 10f, 2 + (k * 13) % 100 / 10f, (k % 5) - 2);
            var b = new RoutePoint(-3 + (k * 11) % 100 / 10f, 2 + (k * 3) % 100 / 10f, (k % 7) - 3);
            Assert.True(RouteCost.LowerBound(a, b) <= RouteCost.Exact(grid, a, b) + 1e-4f);
        }
    }

    [Fact]
    public void Grid_RejectsBadSizes()
    {
        Assert.Throws<ArgumentException>(() => new RouteGrid(new float[3], 2, 2, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouteGrid(new float[4], 2, 2, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouteGrid(Array.Empty<float>(), 0, 2, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => new RouteProblem(Row(0), new float[] { 0 }, new float[] { 0 }, new float[] { float.NaN }));
    }
}
