namespace Miller.Solver;

// Orders the nodes of a RouteProblem into one open path that starts at a given node and ends
// anywhere, as short as the evaluation budget allows in RouteCost units: a nearest-neighbour walk
// over candidate lists (the k planar-nearest nodes of each node, with their exact costs), then
// 2-opt and Or-opt local search over the same candidates until no move improves or the allowance
// is spent. Everything is deterministic: the same problem gives the same path.
public static class RouteSolver
{
    public const int CandidateCount = 10;

    public static int[] Solve(RouteProblem problem, int start, RouteBudget budget, long allowance, CancellationToken cancellation)
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

        var buckets = new SpatialBuckets(problem);
        var candidates = new CandidateLists(problem, buckets, CandidateCount);
        cancellation.ThrowIfCancellationRequested();
        var order = NearestNeighbour(problem, buckets, candidates, start);
        cancellation.ThrowIfCancellationRequested();
        var search = new LocalSearch(problem, candidates, order, allowance, cancellation);
        search.Run();
        budget.Consume(search.Evaluations);
        return order;
    }

    // Path cost in RouteCost units, for tests and statistics.
    public static float PathCost(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var cost = 0f;
        for (var k = 1; k < order.Count; k++)
        {
            cost += RouteCost.Exact(problem.Grid, problem.Node(order[k - 1]), problem.Node(order[k]));
        }

        return cost;
    }

    private static int[] NearestNeighbour(RouteProblem problem, SpatialBuckets buckets, CandidateLists candidates, int start)
    {
        var n = problem.Count;
        var order = new int[n];
        var visited = new bool[n];
        var current = start;
        order[0] = start;
        visited[start] = true;
        buckets.Remove(start);
        for (var step = 1; step < n; step++)
        {
            var best = -1;
            var bestCost = float.PositiveInfinity;
            var slots = candidates.Of(current);
            var costs = candidates.CostsOf(current);
            for (var m = 0; m < slots.Length; m++)
            {
                var c = slots[m];
                if (c < 0)
                {
                    break;
                }

                if (!visited[c] && costs[m] < bestCost)
                {
                    bestCost = costs[m];
                    best = c;
                }
            }

            if (best < 0)
            {
                best = buckets.NearestAlive(problem.Node(current), visited);
            }

            order[step] = best;
            visited[best] = true;
            buckets.Remove(best);
            current = best;
        }

        return order;
    }
}

// The k planar-nearest nodes of every node with the exact cost to each, in one flat array of k
// slots per node; unused slots hold -1.
internal sealed class CandidateLists
{
    private readonly int[] _nodes;
    private readonly float[] _costs;

    public CandidateLists(RouteProblem problem, SpatialBuckets buckets, int k)
    {
        var n = problem.Count;
        K = k;
        _nodes = new int[n * k];
        _costs = new float[n * k];
        Array.Fill(_nodes, -1);
        Span<int> nearest = stackalloc int[k];
        Span<float> distances = stackalloc float[k];
        for (var node = 0; node < n; node++)
        {
            var count = buckets.Nearest(node, k, nearest, distances);
            var from = problem.Node(node);
            for (var m = 0; m < count; m++)
            {
                _nodes[node * k + m] = nearest[m];
                _costs[node * k + m] = RouteCost.Exact(problem.Grid, from, problem.Node(nearest[m]));
            }
        }
    }

    public int K { get; }

    public ReadOnlySpan<int> Of(int node) => _nodes.AsSpan(node * K, K);

    public ReadOnlySpan<float> CostsOf(int node) => _costs.AsSpan(node * K, K);

    // The cached cost when v is a candidate of u or u one of v; NaN otherwise.
    public float Cached(int u, int v)
    {
        var slots = _nodes.AsSpan(u * K, K);
        for (var m = 0; m < K; m++)
        {
            if (slots[m] == v)
            {
                return _costs[u * K + m];
            }
        }

        slots = _nodes.AsSpan(v * K, K);
        for (var m = 0; m < K; m++)
        {
            if (slots[m] == u)
            {
                return _costs[v * K + m];
            }
        }

        return float.NaN;
    }
}
