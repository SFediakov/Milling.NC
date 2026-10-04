using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// T-153: every node where the XY direction changes starts a new movement (any angle, on circles as on
// corners); the first and last 5 mm of a movement run at a third of the speed, a millimetre is slow once
// where zones overlap, route ends clip them, and straight nodes are not fined. The fine is charged on
// XY travel.
public sealed class TurnFineTests
{
    private const float Cell = 0.2f;

    private static RouteProblem Path(params (float X, float Y)[] points)
        => new(new RouteGrid(new float[1], 1, 1, 0, 0, Cell), points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(), new float[points.Length]);

    private static int[] InOrder(RouteProblem problem) => Enumerable.Range(0, problem.Count).ToArray();

    private static float Slow(RouteProblem problem) => TurnFine.SlowLength(problem, InOrder(problem));

    private static bool FinedAt(RouteProblem problem, int k) => TurnFine.IsFined(problem, InOrder(problem), k);

    private static (float X, float Y) Polar(float degrees, float length)
        => (length * MathF.Cos(degrees * MathF.PI / 180f), length * MathF.Sin(degrees * MathF.PI / 180f));

    // A straight chord of 20 mm, then a turn by `degrees`, then 20 mm more.
    private static RouteProblem Corner(float degrees)
    {
        var (x, y) = Polar(degrees, 20f);
        return Path((0, 0), (20, 0), (20 + x, y));
    }

    private static RouteProblem Octagon(float s)
        => Path((0, 0), (s, 0), (2 * s, s), (2 * s, 2 * s), (s, 3 * s), (0, 3 * s), (-s, 2 * s), (-s, s), (0, 0.001f));

    private static float[] Arc(RouteProblem problem)
    {
        var arc = new float[problem.Count];
        for (var k = 1; k < problem.Count; k++)
        {
            arc[k] = arc[k - 1] + RouteCost.Planar(problem.Node(k - 1), problem.Node(k));
        }

        return arc;
    }

    // Whether the direction changes at k, computed here from the rule.
    private static bool Turns(RouteProblem problem, int k)
    {
        var (ux, uy) = (problem.X[k] - problem.X[k - 1], problem.Y[k] - problem.Y[k - 1]);
        var (wx, wy) = (problem.X[k + 1] - problem.X[k], problem.Y[k + 1] - problem.Y[k]);
        var lu = MathF.Sqrt(ux * ux + uy * uy);
        var lw = MathF.Sqrt(wx * wx + wy * wy);
        return lu >= TurnFine.MinChord && lw >= TurnFine.MinChord
            && (MathF.Abs(ux * wy - uy * wx) > TurnFine.StraightSine * (lu * lw) || ux * wx + uy * wy < 0);
    }

    // The union of the zones SlowZone before and after every turning node, within the route.
    private static float UnionOfZones(RouteProblem problem)
    {
        var arc = Arc(problem);
        var zones = Enumerable.Range(1, Math.Max(problem.Count - 2, 0))
            .Where(k => Turns(problem, k))
            .Select(k => (Lo: MathF.Max(0f, arc[k] - TurnFine.SlowZone), Hi: MathF.Min(arc[^1], arc[k] + TurnFine.SlowZone)))
            .OrderBy(z => z.Lo)
            .ToList();
        var union = 0f;
        var open = 0f;
        var close = -1f;
        foreach (var (lo, hi) in zones)
        {
            if (lo > close)
            {
                union += MathF.Max(0f, close - open);
                open = lo;
                close = hi;
            }
            else
            {
                close = MathF.Max(close, hi);
            }
        }

        return union + MathF.Max(0f, close - open);
    }

    [Fact]
    public void OneTurnBetweenLongChords_Slows5MmOnEachSide()
    {
        var problem = Corner(90f);
        Assert.Equal(10f, Slow(problem), 4);
        Assert.Equal(10f * TurnFine.PerSlowMillimetre, TurnFine.Fine(problem, InOrder(problem)), 4);
        // A third of the speed: the slow 10 mm take 30 mm worth of XY travel instead of 10.
        Assert.Equal((10f / TurnFine.SlowSpeedFactor - 10f) / RouteCost.XySpeedFactor, TurnFine.Fine(problem, InOrder(problem)), 4);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 10f)]
    [InlineData(10f, 10f)]
    [InlineData(34.9f, 10f)]
    [InlineData(35.1f, 10f)]
    [InlineData(90f, 10f)]
    [InlineData(179f, 10f)]
    [InlineData(180f, 10f)]
    public void EveryDirectionChange_IsFined(float degrees, float slow)
    {
        Assert.Equal(slow, Slow(Corner(degrees)), 4);
    }

    [Fact]
    public void StraightNodes_AreNotFined_AndTheZoneRunsAcrossShortChords()
    {
        // 1 mm chords along X, a right angle at 20, 1 mm chords along Y: one new direction.
        var points = new List<(float, float)>();
        for (var i = 0; i <= 20; i++)
        {
            points.Add((i, 0));
        }

        for (var j = 1; j <= 20; j++)
        {
            points.Add((20, j));
        }

        var problem = Path(points.ToArray());
        Assert.Equal(10f, Slow(problem), 3);
        Assert.Equal(1, Enumerable.Range(1, problem.Count - 2).Count(k => FinedAt(problem, k)));
    }

    [Fact]
    public void DiagonalOfCellCentres_FarFromTheOrigin_IsStraight_AndAOneCellJogIsNot()
    {
        // Cell centres of a 0.05 mm grid at the far corner of the largest grid (4,000,000 cells, 100 mm
        // at this cell size): float rounding makes the diagonal only nearly collinear, which is not a
        // direction change.
        const float cell = 0.05f;
        var points = Enumerable.Range(0, 30).Select(k => (98f + (k + 0.5f) * cell, 97f + (k + 0.5f) * cell)).ToArray();
        var straight = Path(points);
        Assert.Equal(0f, Slow(straight));
        points[15] = (points[15].Item1 + cell, points[15].Item2);
        var jog = Path(points);
        Assert.Equal(new[] { 14, 15, 16 }, Enumerable.Range(1, jog.Count - 2).Where(k => FinedAt(jog, k)).ToArray());
        Assert.Equal(Arc(jog)[^1], Slow(jog), 3);
    }

    [Theory]
    [InlineData(1f, 11f)]
    [InlineData(3f, 13f)]
    [InlineData(10f, 20f)]
    [InlineData(12f, 20f)]
    public void OverlappingZones_AreSlowOnce(float width, float slow)
    {
        // A U-turn: two right angles `width` apart.
        Assert.Equal(slow, Slow(Path((0, 0), (20, 0), (20, width), (0, width))), 4);
    }

    [Fact]
    public void RouteEnds_ClipTheZones_AndAreNeverFined()
    {
        Assert.Equal(2f + 5f, Slow(Path((0, 0), (2, 0), (2, 20))), 4);
        Assert.Equal(5f + 3f, Slow(Path((0, 0), (20, 0), (20, 3))), 4);
        Assert.Equal(0f, Slow(Path((0, 0), (20, 0))));
        Assert.Equal(0f, Slow(Path((0, 0))));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.05f)]
    [InlineData(3f)]
    public void Circles_AreFinedAtEveryTurn(float side)
    {
        // The turns of the octagon lie closer than two zones, so the whole route is slow.
        var octagon = Octagon(side);
        Assert.All(Enumerable.Range(1, octagon.Count - 2), k => Assert.True(FinedAt(octagon, k), $"node {k} not fined"));
        Assert.Equal(Arc(octagon)[^1], Slow(octagon), 3);
        var arc = Enumerable.Range(0, 5).Select(k => Polar(60f * k, 10f * side)).ToArray();
        Assert.Equal(30f, Slow(Path(arc)), 3);
    }

    [Fact]
    public void SlowLength_EqualsTheUnionOfTheZonesOfEveryDirectionChange()
    {
        var random = new Random(153);
        for (var trial = 0; trial < 300; trial++)
        {
            var count = random.Next(1, 40);
            var points = new (float X, float Y)[count];
            for (var k = 0; k < count; k++)
            {
                points[k] = trial % 2 == 0
                    ? (random.Next(0, 8) * 1.5f, random.Next(0, 8) * 1.5f)
                    : ((float)(random.NextDouble() * 30), (float)(random.NextDouble() * 30));
            }

            var problem = Path(points);
            for (var k = 1; k < count - 1; k++)
            {
                Assert.Equal(Turns(problem, k), FinedAt(problem, k));
            }

            Assert.True(MathF.Abs(UnionOfZones(problem) - Slow(problem)) <= 1e-3f, $"union {UnionOfZones(problem)}, slow length {Slow(problem)}");
        }
    }

    [Fact]
    public void ReversedRoutes_HaveTheSameStatusesAndSlowLength()
    {
        var random = new Random(5150);
        for (var trial = 0; trial < 200; trial++)
        {
            var count = random.Next(3, 40);
            var points = new (float, float)[count];
            for (var k = 0; k < count; k++)
            {
                points[k] = trial % 2 == 0
                    ? (random.Next(0, 12) * 0.2f, random.Next(0, 12) * 0.2f)
                    : ((float)(4 * Math.Cos(k * 0.4) + random.NextDouble() * 0.1), (float)(4 * Math.Sin(k * 0.4) + random.NextDouble() * 0.1));
            }

            var problem = Path(points);
            var forward = InOrder(problem);
            var backward = forward.Reverse().ToArray();
            for (var k = 1; k < count - 1; k++)
            {
                Assert.Equal(TurnFine.IsFined(problem, forward, k), TurnFine.IsFined(problem, backward, count - 1 - k));
            }

            Assert.True(MathF.Abs(TurnFine.SlowLength(problem, forward) - TurnFine.SlowLength(problem, backward)) <= 1e-3f);
        }
    }

    [Fact]
    public void Fine_DependsOnXyOnly()
    {
        var flat = Path((0, 0), (20, 0), (20, 3), (0, 3));
        var raised = new RouteProblem(flat.Grid, flat.X, flat.Y, new[] { 0f, 4f, -2f, 7f });
        Assert.Equal(13f, Slow(flat), 4);
        Assert.Equal(Slow(flat), Slow(raised));
    }

    [Fact]
    public void CoincidentNodes_GiveNoTurn_AndAFiniteLength()
    {
        var problem = Path((0, 0), (5, 0), (5, 0), (5, 5), (5, 5));
        var slow = Slow(problem);
        Assert.True(float.IsFinite(slow));
        Assert.Equal(0f, slow);
        Assert.False(FinedAt(problem, 1));
        Assert.False(FinedAt(problem, 2));
    }
}
