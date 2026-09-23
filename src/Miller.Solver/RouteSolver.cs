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
// gives the same path.
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

        var candidates = new CandidateLists(problem, new SpatialBuckets(problem), CandidateCount);
        cancellation.ThrowIfCancellationRequested();
        var nearest = NearestNeighbour(problem, new SpatialBuckets(problem), candidates, start);
        cancellation.ThrowIfCancellationRequested();
        var smooth = SmoothWalk(problem, new SpatialBuckets(problem), candidates, start);
        cancellation.ThrowIfCancellationRequested();
        var order = PathCost(problem, smooth) < PathCost(problem, nearest) ? smooth : nearest;
        var search = new LocalSearch(problem, candidates, order, allowance, cancellation);
        search.Run();
        budget.Consume(search.Evaluations);
        return order;
    }

    // Travel plus turn fine in RouteCost units, the value the solver minimises; for tests and statistics.
    public static float PathCost(RouteProblem problem, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(order);
        var cost = 0f;
        for (var k = 1; k < order.Count; k++)
        {
            cost += RouteCost.Exact(problem.Grid, problem.Node(order[k - 1]), problem.Node(order[k]));
        }

        return cost + TurnFine.Fine(problem, order);
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

    // A candidate is scored by its step and the cheapest step after it, fine increase included: the
    // fine of a turn is only known once the move after it is chosen, so a one-step walk would never
    // see that a short move across a row costs a right angle on the next step. A candidate whose own
    // candidates are all visited is scored with the lower bound to the nearest node still unvisited.
    // Appending a node settles the turn at the last node and may make the one before it circular, so
    // the fine increase is the windowed term over the last two nodes and the appended ones; a node two
    // places behind the end has its final status and is recorded for the walks of the window.
    private static int[] SmoothWalk(RouteProblem problem, SpatialBuckets buckets, CandidateLists candidates, int start)
    {
        var n = problem.Count;
        var order = new int[n];
        var visited = new bool[n];
        var fined = new bool[n];
        var lengths = new float[n];
        var window = new FineWindow(fined);
        var view = new PathView(problem);
        var changed = new int[4];
        var current = start;
        order[0] = start;
        visited[start] = true;
        buckets.Remove(start);
        for (var step = 1; step < n; step++)
        {
            var count = 0;
            for (var p = Math.Max(0, step - 2); p <= Math.Min(step + 1, n - 1); p++)
            {
                changed[count++] = p;
            }

            window.Prepare(order, lengths, Math.Min(step + 2, n), changed, count);
            var before = Term(window, view, order, lengths, step - 1);
            var best = -1;
            var bestValue = float.PositiveInfinity;
            var slots = candidates.Of(current);
            var costs = candidates.CostsOf(current);
            for (var m = 0; m < slots.Length; m++)
            {
                var c = slots[m];
                if (c < 0)
                {
                    break;
                }

                if (visited[c])
                {
                    continue;
                }

                order[step] = c;
                lengths[step - 1] = RouteCost.Planar(problem.Node(current), problem.Node(c));
                visited[c] = true;
                var next = float.PositiveInfinity;
                if (step + 1 < n)
                {
                    var nextSlots = candidates.Of(c);
                    var nextCosts = candidates.CostsOf(c);
                    for (var m2 = 0; m2 < nextSlots.Length; m2++)
                    {
                        var d = nextSlots[m2];
                        if (d < 0)
                        {
                            break;
                        }

                        if (visited[d])
                        {
                            continue;
                        }

                        order[step + 1] = d;
                        lengths[step] = RouteCost.Planar(problem.Node(c), problem.Node(d));
                        next = MathF.Min(next, nextCosts[m2] + (Term(window, view, order, lengths, step + 1) - before) * TurnFine.PerSlowMillimetre);
                    }
                }

                if (float.IsPositiveInfinity(next))
                {
                    var alive = step + 1 < n ? buckets.NearestAlive(problem.Node(c), visited) : -1;
                    next = (Term(window, view, order, lengths, step) - before) * TurnFine.PerSlowMillimetre
                        + (alive >= 0 ? RouteCost.LowerBound(problem.Node(c), problem.Node(alive)) : 0f);
                }

                visited[c] = false;
                var value = costs[m] + next;
                if (value < bestValue)
                {
                    bestValue = value;
                    best = c;
                }
            }

            if (best < 0)
            {
                best = buckets.NearestAlive(problem.Node(current), visited);
            }

            order[step] = best;
            lengths[step - 1] = RouteCost.Planar(problem.Node(current), problem.Node(best));
            visited[best] = true;
            buckets.Remove(best);
            if (step >= 3)
            {
                fined[order[step - 2]] = TurnFine.IsFined(problem, step >= 4 ? order[step - 4] : -1, order[step - 3], order[step - 2], order[step - 1], best);
            }

            current = best;
        }

        return order;
    }

    // Windowed fine term of order[0 .. end]. The window of a step is prepared over the last two nodes
    // of the route and every node the step may append, so all routes compared in one step share it.
    private static float Term(FineWindow window, PathView view, int[] order, float[] lengths, int end)
    {
        view.Reset(order, lengths);
        view.Add(0, end, false);
        return window.Term(view, true);
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
