using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// T-150: precedence pairs order one node ahead of another; the walks visit a node only once its
// predecessors are visited and the local search applies a move only when the route it produces
// keeps every pair. Without pairs the solver is unchanged.
public sealed class RouteSolverPrecedenceTests
{
    private static RouteGrid Flat(int width, int height, float cell)
        => new(new float[width * height], width, height, 0, 0, cell);

    private static (float[] X, float[] Y) Lattice(RouteGrid grid, int step)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        for (var j = 0; j < grid.Height; j += step)
        {
            for (var i = 0; i < grid.Width; i += step)
            {
                xs.Add(grid.OriginX + (i + 0.5f) * grid.CellSize);
                ys.Add(grid.OriginY + (j + 0.5f) * grid.CellSize);
            }
        }

        return (xs.ToArray(), ys.ToArray());
    }

    private static int[] Solve(RouteProblem problem, int start)
        => RouteSolver.Solve(problem, start, new RouteBudget(2_000_000), 2_000_000, TestContext.Current.CancellationToken);

    private static void AssertKeepsPairs(int[] order, RouteProblem problem)
    {
        var position = new int[order.Length];
        for (var k = 0; k < order.Length; k++)
        {
            position[order[k]] = k;
        }

        for (var k = 0; k < problem.PairCount; k++)
        {
            Assert.True(position[problem.Before[k]] < position[problem.After[k]], $"pair {k}: {problem.Before[k]} must come before {problem.After[k]}");
        }
    }

    [Fact]
    public void RandomPairs_AreKeptByTheWalksAndEveryAppliedMove()
    {
        var grid = Flat(30, 30, 1f);
        var (xs, ys) = Lattice(grid, 2);
        var n = xs.Length;
        var random = new Random(7);
        var before = new List<int>();
        var after = new List<int>();
        // Pairs that agree with the node index, so they are acyclic and node 0 has no predecessor.
        for (var k = 0; k < 60; k++)
        {
            var a = random.Next(0, n - 1);
            var b = random.Next(a + 1, n);
            before.Add(a);
            after.Add(b);
        }

        var problem = new RouteProblem(grid, xs, ys, new float[n]) { Before = before.ToArray(), After = after.ToArray() };
        var order = Solve(problem, 0);
        Assert.Equal(n, order.Distinct().Count());
        Assert.Equal(0, order[0]);
        AssertKeepsPairs(order, problem);
    }

    // Pairs the free route already satisfies cost nothing: the constrained route is as short.
    [Fact]
    public void PairsTheFreeRouteSatisfies_KeepTheCost()
    {
        var grid = Flat(30, 30, 1f);
        var (xs, ys) = Lattice(grid, 2);
        var free = new RouteProblem(grid, xs, ys, new float[xs.Length]);
        var freeOrder = Solve(free, 0);
        var freeCost = RouteSolver.PathCost(free, freeOrder);

        var before = new List<int>();
        var after = new List<int>();
        for (var k = 0; k + 5 < freeOrder.Length; k += 7)
        {
            before.Add(freeOrder[k]);
            after.Add(freeOrder[k + 5]);
        }

        var constrained = new RouteProblem(grid, xs, ys, new float[xs.Length]) { Before = before.ToArray(), After = after.ToArray() };
        var order = Solve(constrained, 0);
        AssertKeepsPairs(order, constrained);
        var cost = RouteSolver.PathCost(constrained, order);
        Assert.True(cost <= freeCost * 1.1f + 1e-3f, $"constrained {cost} against free {freeCost}");
    }

    // A pair that reverses the natural order forces the detour: the far node is visited first.
    [Fact]
    public void APair_MakesTheFarNodeComeFirst()
    {
        var grid = Flat(10, 1, 1f);
        var xs = Enumerable.Range(0, 10).Select(i => i + 0.5f).ToArray();
        var ys = Enumerable.Repeat(0.5f, 10).ToArray();
        var problem = new RouteProblem(grid, xs, ys, new float[10]) { Before = new[] { 9 }, After = new[] { 1 } };
        var order = Solve(problem, 0);
        AssertKeepsPairs(order, problem);
        Assert.True(Array.IndexOf(order, 9) < Array.IndexOf(order, 1));
    }

    [Fact]
    public void CyclicPairs_OrAStartWithAPredecessor_AreRefused()
    {
        var grid = Flat(10, 1, 1f);
        var xs = Enumerable.Range(0, 10).Select(i => i + 0.5f).ToArray();
        var ys = Enumerable.Repeat(0.5f, 10).ToArray();
        var cyclic = new RouteProblem(grid, xs, ys, new float[10]) { Before = new[] { 2, 3 }, After = new[] { 3, 2 } };
        Assert.Throws<ArgumentException>(() => Solve(cyclic, 0));
        var startLast = new RouteProblem(grid, xs, ys, new float[10]) { Before = new[] { 5 }, After = new[] { 0 } };
        Assert.Throws<ArgumentException>(() => Solve(startLast, 0));
        var outOfRange = new RouteProblem(grid, xs, ys, new float[10]) { Before = new[] { 5 }, After = new[] { 10 } };
        Assert.ThrowsAny<ArgumentException>(() => Solve(outOfRange, 0));
        var uneven = new RouteProblem(grid, xs, ys, new float[10]) { Before = new[] { 5 }, After = Array.Empty<int>() };
        Assert.Throws<ArgumentException>(() => Solve(uneven, 0));
    }
}
