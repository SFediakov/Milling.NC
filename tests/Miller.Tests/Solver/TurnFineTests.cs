using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// A turn of more than 35 degrees slows the 5 mm before and after it to 0.3 of the speed; the zones
// run across short chords, overlapping zones are slow once, route ends clip them, and a real
// circular move (four nodes on one circle, turning the same way, each turn below 90 degrees) is not
// fined. The fine is charged on XY travel.
public sealed class TurnFineTests
{
    private const float Cell = 0.2f;

    private static RouteProblem Path(params (float X, float Y)[] points)
        => new(new RouteGrid(new float[1], 1, 1, 0, 0, Cell), points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(), new float[points.Length]);

    private static int[] InOrder(RouteProblem problem) => Enumerable.Range(0, problem.Count).ToArray();

    private static float Slow(RouteProblem problem) => TurnFine.SlowLength(problem, InOrder(problem));

    private static bool FinedAt(RouteProblem problem, int k)
        => TurnFine.IsFined(problem, k >= 2 ? k - 2 : -1, k - 1, k, k + 1, k + 2 < problem.Count ? k + 2 : -1);

    private static (float X, float Y) Polar(float degrees, float length)
        => (length * MathF.Cos(degrees * MathF.PI / 180f), length * MathF.Sin(degrees * MathF.PI / 180f));

    // A straight chord of 20 mm, then a turn by `degrees`, then 20 mm more.
    private static RouteProblem Corner(float degrees)
    {
        var (x, y) = Polar(degrees, 20f);
        return Path((0, 0), (20, 0), (20 + x, y));
    }

    [Fact]
    public void OneSharpTurnBetweenLongChords_Slows5MmOnEachSide()
    {
        var problem = Corner(90f);
        Assert.Equal(10f, Slow(problem), 4);
        Assert.Equal(10f * TurnFine.PerSlowMillimetre, TurnFine.Fine(problem, InOrder(problem)), 4);
        // 0.3 of the speed: the slow 10 mm take 10 / 0.3 instead of 10 mm of XY travel.
        Assert.Equal((10f / TurnFine.SlowSpeedFactor - 10f) / RouteCost.XySpeedFactor, TurnFine.Fine(problem, InOrder(problem)), 4);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(20f, 0f)]
    [InlineData(34.9f, 0f)]
    [InlineData(35.1f, 10f)]
    [InlineData(90f, 10f)]
    [InlineData(179f, 10f)]
    public void OnlyTurnsAbove35Degrees_AreFined(float degrees, float slow)
    {
        Assert.Equal(slow, Slow(Corner(degrees)), 4);
    }

    [Fact]
    public void Zone_RunsAcrossShortChords()
    {
        // 1 mm chords along X, a right angle at 20, 1 mm chords along Y.
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

    [Theory]
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

    [Fact]
    public void LatticeOctagon_IsARealCircularMove()
    {
        // Eight cell centres on the circle of radius sqrt(2.5) cells around (0.5, 1.5), 45 degree turns.
        var s = 2f;
        var problem = Path((0, 0), (s, 0), (2 * s, s), (2 * s, 2 * s), (s, 3 * s), (0, 3 * s), (-s, 2 * s), (-s, s), (0, 0.001f));
        Assert.All(Enumerable.Range(1, problem.Count - 2), k => Assert.False(FinedAt(problem, k), $"node {k} fined"));
        Assert.Equal(0f, Slow(problem));
    }

    [Fact]
    public void HexagonArc_IsCircular_ButTheSameTurnsOffTheCircleAreFined()
    {
        var arc = Enumerable.Range(0, 5).Select(k => Polar(60f * k, 10f)).ToArray();
        Assert.Equal(0f, Slow(Path(arc)));
        arc[2] = (arc[2].Item1 * 1.1f, arc[2].Item2 * 1.1f);
        Assert.True(Slow(Path(arc)) > 0f);
    }

    [Fact]
    public void AxisToAxisTurns_AreNeverCircular()
    {
        // Square corners and a rectangle U-turn lie on a circle, but every turn is 90 degrees.
        var square = Path((0, 0), (4, 0), (4, 4), (0, 4), (0, 0.5f));
        Assert.True(FinedAt(square, 1));
        Assert.True(FinedAt(square, 2));
        Assert.True(FinedAt(square, 3));
        var uTurn = Path((0, 0), (10, 0), (10, 1), (0, 1));
        Assert.True(FinedAt(uTurn, 1));
        Assert.True(FinedAt(uTurn, 2));
    }

    [Fact]
    public void AlternatingKinks_AreNotCircular()
    {
        // 45 degree kinks left and right: a staircase, not an arc.
        var problem = Path((0, 0), (1, 0), (2, 1), (3, 1), (4, 2), (5, 2));
        Assert.All(Enumerable.Range(1, problem.Count - 2), k => Assert.True(FinedAt(problem, k), $"node {k} not fined"));
    }

    [Fact]
    public void ReversedRoute_HasTheSameStatusesAndSlowLength()
    {
        var random = new Random(12345);
        for (var trial = 0; trial < 200; trial++)
        {
            var count = random.Next(3, 30);
            var points = new (float, float)[count];
            for (var k = 0; k < count; k++)
            {
                // Lattice points so that concyclic windows occur as well as random turns.
                points[k] = (random.Next(0, 8) * 1.5f, random.Next(0, 8) * 1.5f);
            }

            var problem = Path(points);
            var forward = InOrder(problem);
            var backward = forward.Reverse().ToArray();
            for (var k = 1; k < count - 1; k++)
            {
                var p = k >= 2 ? k - 2 : -1;
                var d = k + 2 < count ? k + 2 : -1;
                Assert.Equal(TurnFine.IsFined(problem, p, k - 1, k, k + 1, d), TurnFine.IsFined(problem, d, k + 1, k, k - 1, p));
            }

            var there = TurnFine.SlowLength(problem, forward);
            var back = TurnFine.SlowLength(problem, backward);
            Assert.True(MathF.Abs(there - back) <= 1e-3f, $"forward {there}, backward {back}");
        }
    }

    [Fact]
    public void SlowLength_EqualsTheUnionOfTheZones()
    {
        var random = new Random(777);
        for (var trial = 0; trial < 300; trial++)
        {
            var count = random.Next(2, 40);
            var points = new (float, float)[count];
            for (var k = 0; k < count; k++)
            {
                points[k] = ((float)(random.NextDouble() * 30), (float)(random.NextDouble() * 30));
            }

            var problem = Path(points);
            var arc = new float[count];
            for (var k = 1; k < count; k++)
            {
                arc[k] = arc[k - 1] + RouteCost.Planar(problem.Node(k - 1), problem.Node(k));
            }

            var zones = Enumerable.Range(1, Math.Max(count - 2, 0))
                .Where(k => FinedAt(problem, k))
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

            union += MathF.Max(0f, close - open);
            Assert.True(MathF.Abs(union - Slow(problem)) <= 1e-3f, $"union {union}, slow length {Slow(problem)}");
        }
    }

    [Fact]
    public void Fine_DependsOnXyOnly()
    {
        var flat = Path((0, 0), (20, 0), (20, 3), (0, 3));
        var raised = new RouteProblem(flat.Grid, flat.X, flat.Y, new[] { 0f, 4f, -2f, 7f });
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
