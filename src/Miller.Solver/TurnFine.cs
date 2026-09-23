namespace Miller.Solver;

// The time fine of a direction change. A movement is the stretch of a route between two fined turns
// or a route end; its first and last SlowZone millimetres run at SlowSpeedFactor of the speed, so a
// fined turn slows SlowZone before it and SlowZone after it, zones that overlap are slow once and a
// zone stops at a route end. A turn is fined when the XY direction of the chords between consecutive
// nodes changes by more than SharpTurnDegrees, unless it belongs to a real circular move: four
// consecutive nodes on one circle (within CircleToleranceCells of a cell), turning the same way at
// both inner nodes, each of the two turns below CircularMaxTurnDegrees, so a square corner or a
// U-turn is never circular. The fine is charged on XY travel; Z travel keeps the Z speed of RouteCost.
public static class TurnFine
{
    public const float SharpTurnDegrees = 35f;

    public const float SlowZone = 5f;

    public const float SlowSpeedFactor = 0.3f;

    public const float CircularMaxTurnDegrees = 90f;

    public const float CircleToleranceCells = 0.5f;

    // A chord shorter than this has no direction, so no turn is measured at its ends.
    public const float MinChord = 1e-5f;

    // RouteCost units a slow millimetre of XY travel costs on top of its normal cost.
    public const float PerSlowMillimetre = (1f / SlowSpeedFactor - 1f) / RouteCost.XySpeedFactor;

    // Computed in double: the float cosine of 90 degrees is slightly negative and would let an
    // exact axis-to-axis turn pass as circular.
    private static readonly float CosSharp = (float)Math.Cos(SharpTurnDegrees * Math.PI / 180.0);

    private static readonly float CosCircularMax = (float)Math.Cos(CircularMaxTurnDegrees * Math.PI / 180.0);

    // Whether the turn at b between the chords a-b and b-c is fined; p precedes a and d follows c,
    // -1 where the route has no such node. Reversing the five nodes gives the same answer.
    public static bool IsFined(RouteProblem problem, int p, int a, int b, int c, int d)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var x = problem.X;
        var y = problem.Y;
        if (!Turn(x, y, a, b, c, out var cos, out _) || !(cos < CosSharp))
        {
            return false;
        }

        if (!(cos > CosCircularMax))
        {
            return true;
        }

        var tolerance = CircleToleranceCells * problem.Grid.CellSize;
        return !((p >= 0 && Arc(x, y, p, a, b, c, tolerance)) || (d >= 0 && Arc(x, y, a, b, c, d, tolerance)));
    }

    // Slow XY length of a route: 2 x SlowZone per fined turn less the overlap of every two
    // consecutive events, the route start and end being events without a zone.
    public static float SlowLength(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var n = order.Count;
        if (n < 3)
        {
            return 0f;
        }

        var s = 0f;
        var lastS = 0f;
        var lastZones = 0;
        var total = 0f;
        for (var k = 1; k < n; k++)
        {
            s += RouteCost.Planar(problem.Node(order[k - 1]), problem.Node(order[k]));
            int zones;
            if (k == n - 1)
            {
                zones = 0;
            }
            else if (IsFined(problem, k >= 2 ? order[k - 2] : -1, order[k - 1], order[k], order[k + 1], k + 2 < n ? order[k + 2] : -1))
            {
                zones = 1;
            }
            else
            {
                continue;
            }

            total += zones * 2f * SlowZone - Overlap(lastZones, zones, s - lastS);
            lastS = s;
            lastZones = zones;
        }

        return total;
    }

    // The fine of a whole route in RouteCost units.
    public static float Fine(RouteProblem problem, IReadOnlyList<int> order) => SlowLength(problem, order) * PerSlowMillimetre;

    // Length slow twice for two consecutive events `gap` apart, each with 0 (route end) or 1 zone.
    public static float Overlap(int zonesA, int zonesB, float gap) => MathF.Max(0f, SlowZone * (zonesA + zonesB) - gap);

    // Cosine of the XY direction change at b and its side (+1 left, -1 right, 0 straight); false when
    // a chord has no direction. Swapping a and c keeps the cosine and flips the side exactly.
    private static bool Turn(float[] x, float[] y, int a, int b, int c, out float cos, out int side)
    {
        var ux = x[b] - x[a];
        var uy = y[b] - y[a];
        var wx = x[c] - x[b];
        var wy = y[c] - y[b];
        var lu = MathF.Sqrt(ux * ux + uy * uy);
        var lw = MathF.Sqrt(wx * wx + wy * wy);
        if (lu < MinChord || lw < MinChord)
        {
            cos = 1f;
            side = 0;
            return false;
        }

        cos = (ux * wx + uy * wy) / (lu * lw);
        var cross = ux * wy - uy * wx;
        side = cross > 0 ? 1 : cross < 0 ? -1 : 0;
        return true;
    }

    // Four consecutive nodes of a real circular move: both inner turns exist, lie below
    // CircularMaxTurnDegrees and go the same way, and each end node lies on the circle through the
    // other three, so the test reads the same in both directions.
    private static bool Arc(float[] x, float[] y, int q0, int q1, int q2, int q3, float tolerance)
    {
        if (!Turn(x, y, q0, q1, q2, out var cos1, out var side1) || !Turn(x, y, q1, q2, q3, out var cos2, out var side2))
        {
            return false;
        }

        if (side1 == 0 || side1 != side2 || !(cos1 > CosCircularMax) || !(cos2 > CosCircularMax))
        {
            return false;
        }

        return OnCircle(x, y, q0, q1, q2, q3, tolerance) && OnCircle(x, y, q1, q2, q3, q0, tolerance);
    }

    // Whether `test` lies within the tolerance of the circle through i, j and k. The three are taken in
    // index order, so a triple always gives the same circle whatever order the route visits it in.
    private static bool OnCircle(float[] x, float[] y, int i, int j, int k, int test, float tolerance)
    {
        if (i > j)
        {
            (i, j) = (j, i);
        }

        if (j > k)
        {
            (j, k) = (k, j);
        }

        if (i > j)
        {
            (i, j) = (j, i);
        }

        double bx = x[j] - x[i];
        double by = y[j] - y[i];
        double cx = x[k] - x[i];
        double cy = y[k] - y[i];
        var d = 2.0 * (bx * cy - by * cx);
        if (d == 0.0)
        {
            return false;
        }

        var b2 = bx * bx + by * by;
        var c2 = cx * cx + cy * cy;
        var ox = (cy * b2 - by * c2) / d;
        var oy = (bx * c2 - cx * b2) / d;
        var radius = Math.Sqrt(ox * ox + oy * oy);
        var tx = x[test] - x[i] - ox;
        var ty = y[test] - y[i] - oy;
        return Math.Abs(Math.Sqrt(tx * tx + ty * ty) - radius) <= tolerance;
    }
}
