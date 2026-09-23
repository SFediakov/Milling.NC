using Miller.Solver.Native;

namespace Miller.Solver;

// The time fine of a direction change. A movement is the stretch of a route between two fined turns
// or a route end; its first and last SlowZone millimetres run at SlowSpeedFactor of the speed, so a
// fined turn slows SlowZone before it and SlowZone after it, zones that overlap are slow once and a
// zone stops at a route end. A turn is fined when the XY direction of the chords between consecutive
// nodes changes by more than SharpTurnDegrees, unless it belongs to a real circular move: four
// consecutive nodes on one circle (within CircleToleranceCells of a cell), turning the same way at
// both inner nodes, each of the two turns below CircularMaxTurnDegrees, so a square corner or a
// U-turn is never circular. The fine is charged on XY travel; Z travel keeps the Z speed of RouteCost.
// Computed by the native library.
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

    // Whether the turn at b between the chords a-b and b-c is fined; p precedes a and d follows c,
    // -1 where the route has no such node. Reversing the five nodes gives the same answer.
    public static unsafe bool IsFined(RouteProblem problem, int p, int a, int b, int c, int d)
    {
        ArgumentNullException.ThrowIfNull(problem);
        fixed (float* x = problem.X, y = problem.Y)
        {
            return SolverNative.mn_turn_is_fined(x, y, problem.Grid.CellSize, p, a, b, c, d) != 0;
        }
    }

    // Slow XY length of a route: 2 x SlowZone per fined turn less the overlap of every two
    // consecutive events, the route start and end being events without a zone.
    public static unsafe float SlowLength(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var nodes = order.ToArray();
        fixed (float* x = problem.X, y = problem.Y)
        fixed (int* list = nodes)
        {
            return SolverNative.mn_turn_slow_length(x, y, problem.Grid.CellSize, list, nodes.Length);
        }
    }

    // The fine of a whole route in RouteCost units.
    public static unsafe float Fine(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var nodes = order.ToArray();
        fixed (float* x = problem.X, y = problem.Y)
        fixed (int* list = nodes)
        {
            return SolverNative.mn_turn_fine(x, y, problem.Grid.CellSize, list, nodes.Length);
        }
    }

    // Length slow twice for two consecutive events `gap` apart, each with 0 (route end) or 1 zone.
    public static float Overlap(int zonesA, int zonesB, float gap) => SolverNative.mn_turn_overlap(zonesA, zonesB, gap);
}
