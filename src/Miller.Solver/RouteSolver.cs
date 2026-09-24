using Miller.Solver.Native;

namespace Miller.Solver;

// Orders the nodes of a RouteProblem into one open path that starts at a given node and ends
// anywhere, as short as the evaluation budget allows in RouteCost units, the turn fine of TurnFine
// included. Two walks over candidate lists (the k planar-nearest nodes of each node, with their
// exact costs) start the route: the nearest-neighbour walk, and a smooth walk that also weighs the
// fine of the next turn; the one with the lower cost is improved by 2-opt and Or-opt local search
// over the same candidates until no move improves or the allowance is spent. Local moves cannot
// leave a stretch whose turns all lie within one slow zone of each other (removing one turn frees
// nothing), so the start decides how many such stretches the route keeps: the smooth walk wins on
// open regions it can cross in straight runs and arcs, the nearest-neighbour walk wherever avoiding
// a turn would only strand nodes for a long way back. Everything is deterministic: the same problem
// gives the same path. The native library (mn_route_solve) runs it; cancellation is polled between
// the phases and every 65536 evaluations.
public static class RouteSolver
{
    public const int CandidateCount = 10;

    public static unsafe int[] Solve(RouteProblem problem, int start, RouteBudget budget, long allowance, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(budget);
        var n = problem.Count;
        if (n == 0)
        {
            throw new ArgumentException("The problem has no nodes.", nameof(problem));
        }

        if (start < 0 || start >= n)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, $"Start must index one of the {n} nodes.");
        }

        if (n == 1)
        {
            return new[] { start };
        }

        var order = new int[n];
        var grid = SolverNative.Grid.Of(problem.Grid);
        long evaluations = 0;
        using var flag = new SolverNative.CancelFlag(cancellation);
        fixed (float* floor = problem.Grid.Floor, x = problem.X, y = problem.Y, z = problem.Z)
        fixed (int* result = order)
        {
            SolverNative.Check(SolverNative.mn_route_solve(&grid, floor, x, y, z, n, start, allowance, flag.Pointer, result, &evaluations), cancellation);
        }

        budget.Consume(evaluations);
        return order;
    }

    // Travel plus turn fine in RouteCost units, the value the solver minimises; for tests and statistics.
    public static unsafe float PathCost(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var nodes = order.ToArray();
        var grid = SolverNative.Grid.Of(problem.Grid);
        fixed (float* floor = problem.Grid.Floor, x = problem.X, y = problem.Y, z = problem.Z)
        fixed (int* list = nodes)
        {
            return SolverNative.mn_route_path_cost(&grid, floor, x, y, z, list, nodes.Length);
        }
    }
}
