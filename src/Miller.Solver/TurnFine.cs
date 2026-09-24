using Miller.Solver.Native;

namespace Miller.Solver;

// The time fine of a direction change. A movement is the stretch of a route between two fined turns
// or a route end; its first and last SlowZone millimetres run at SlowSpeedFactor of the speed, so a
// fined turn slows SlowZone before it and SlowZone after it, zones that overlap are slow once and a
// zone stops at a route end. A node is fined when the XY direction of the chords at it changes by
// more than SharpTurnDegrees (a sharp turn), or when it is one of up to CompoundTurns consecutive
// smaller turns (straight nodes between them skipped, at most CompoundReach positions each way) that
// change the direction by more than SharpTurnDegrees within less than CompoundSpan of path (a
// compound turn; a sharp turn ends the search). Neither is fined on a real circular move: a chain of
// arcs, each four consecutive nodes on one circle (within CircleToleranceCells of a cell) turning the
// same way below CircularMaxTurnDegrees at both inner nodes, at least CircularMinLength long from its
// first node to its last (followed at most ChainReach arcs each way), so a square corner, a U-turn
// or a short arc is never circular. The fine is charged on XY travel; Z travel keeps the Z speed of
// RouteCost. Computed by the native library.
public static class TurnFine
{
    public const float SharpTurnDegrees = 35f;

    public const float SlowZone = 5f;

    public const float SlowSpeedFactor = 0.3f;

    public const float CircularMaxTurnDegrees = 90f;

    public const float CircleToleranceCells = 0.5f;

    public const float CircularMinLength = 10f;

    public const int ChainReach = 16;

    public const int CompoundTurns = 4;

    public const int CompoundReach = 8;

    public const float CompoundSpan = 10f;

    // A chord shorter than this has no direction, so no turn is measured at its ends.
    public const float MinChord = 1e-5f;

    // RouteCost units a slow millimetre of XY travel costs on top of its normal cost.
    public const float PerSlowMillimetre = (1f / SlowSpeedFactor - 1f) / RouteCost.XySpeedFactor;

    // Whether the node at `position` of the route `order` is fined. The reversed route gives the same
    // answer for the same node.
    public static unsafe bool IsFined(RouteProblem problem, IReadOnlyList<int> order, int position)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var nodes = order.ToArray();
        fixed (float* x = problem.X, y = problem.Y)
        fixed (int* list = nodes)
        {
            return SolverNative.mn_turn_fined_at(x, y, problem.Grid.CellSize, list, nodes.Length, position) != 0;
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
