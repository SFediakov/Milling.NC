namespace Miller.Solver;

// How many candidate moves the local search of one generated program may evaluate in total. The
// budget is shared by every route instance of the program; each instance takes a share in
// proportion to its node count out of the nodes still to be routed, so an instance that converges
// early leaves its rest to the later ones.
public sealed class RouteBudget
{
    public const long MaxEvaluations = 40_000_000;

    public RouteBudget(long total)
    {
        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), total, "The budget must not be negative.");
        }

        Total = total;
    }

    public long Total { get; }

    public long Used { get; private set; }

    public long Remaining => Total - Used;

    public long Share(int nodes, long nodesLeft)
    {
        if (nodes < 0 || nodesLeft < nodes)
        {
            throw new ArgumentOutOfRangeException(nameof(nodes), $"{nodes} nodes of {nodesLeft} left.");
        }

        return Native.SolverNative.mn_budget_share(Remaining, nodes, nodesLeft);
    }

    internal void Consume(long evaluations)
    {
        Used += evaluations;
    }
}
