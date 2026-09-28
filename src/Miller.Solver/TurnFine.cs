using Miller.Solver.Native;

namespace Miller.Solver;

// The time fine of a direction change (T-153). A movement is the stretch between two nodes where the
// XY direction of the chords changes (any angle, on circles as on corners) or a route end; its first
// and last SlowZone millimetres run at SlowSpeedFactor of the speed, so every new coordinate set slows
// the SlowZone before and after it, a millimetre is slow once when zones overlap (a movement shorter
// than two zones is slow over its whole length) and route ends clip the zones. Short steps therefore
// run slow along their whole length and the solver keeps long straight moves. A node straight on
// (sine of the change within StraightSine, reversing excluded) is not fined. The fine is charged on
// XY travel; Z travel keeps the Z speed of RouteCost. Computed by the native library.
public static class TurnFine
{
    public const float SlowZone = 5f;

    public const float SlowSpeedFactor = 0.3f;

    // A direction change with a smaller sine is straight on: float noise of the node coordinates.
    public const float StraightSine = 1e-3f;

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
            return SolverNative.mn_turn_fined_at(x, y, list, nodes.Length, position) != 0;
        }
    }

    // Slow XY length of a route: 2 x SlowZone per fined node less the overlap of every two
    // consecutive events, the route start and end being events without a zone.
    public static unsafe float SlowLength(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var nodes = order.ToArray();
        fixed (float* x = problem.X, y = problem.Y)
        fixed (int* list = nodes)
        {
            return SolverNative.mn_turn_slow_length(x, y, list, nodes.Length);
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
            return SolverNative.mn_turn_fine(x, y, list, nodes.Length);
        }
    }

    // Length slow twice for two consecutive events `gap` apart, each with 0 (route end) or 1 zone.
    public static float Overlap(int zonesA, int zonesB, float gap) => SolverNative.mn_turn_overlap(zonesA, zonesB, gap);
}
