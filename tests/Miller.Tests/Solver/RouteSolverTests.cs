using System.Diagnostics;
using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// The solver returns a permutation that starts at the start node, never worse than the
// nearest-neighbour walk, deterministic, within the evaluation allowance, and it prefers the gap
// in a wall to climbing over it.
public sealed class RouteSolverTests
{
    private static RouteGrid Flat(int width, int height, float cell)
        => new(new float[width * height], width, height, 0, 0, cell);

    // Lattice nodes on cell centers every `step` cells.
    private static RouteProblem Lattice(RouteGrid grid, int step)
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

        return new RouteProblem(grid, xs.ToArray(), ys.ToArray(), new float[xs.Count]);
    }

    private static void AssertPermutation(int[] order, int count, int start)
    {
        Assert.Equal(count, order.Length);
        Assert.Equal(start, order[0]);
        Assert.Equal(count, order.Distinct().Count());
        Assert.All(order, k => Assert.InRange(k, 0, count - 1));
    }

    [Fact]
    public void Solve_ReturnsAPermutationFromTheStart_NoWorseThanNearestNeighbour()
    {
        var problem = Lattice(Flat(40, 40, 0.5f), 2);
        var start = 7;
        var greedy = RouteSolver.Solve(problem, start, new RouteBudget(0), 0, TestContext.Current.CancellationToken);
        var budget = new RouteBudget(RouteBudget.MaxEvaluations);
        var improved = RouteSolver.Solve(problem, start, budget, budget.Share(problem.Count, problem.Count), TestContext.Current.CancellationToken);
        AssertPermutation(greedy, problem.Count, start);
        AssertPermutation(improved, problem.Count, start);
        var greedyCost = RouteSolver.PathCost(problem, greedy);
        var improvedCost = RouteSolver.PathCost(problem, improved);
        Assert.True(improvedCost <= greedyCost + 1e-3f, $"improved {improvedCost} > greedy {greedyCost}");
        Assert.True(budget.Used > 0);
        // 400 lattice points 1 mm apart: a full boustrophedon needs 399 unit moves, 133 in XY time.
        Assert.InRange(improvedCost, 399 / RouteCost.XySpeedFactor, 1.15f * 399 / RouteCost.XySpeedFactor);
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        var problem = Lattice(Flat(30, 30, 1f), 3);
        var first = RouteSolver.Solve(problem, 0, new RouteBudget(1_000_000), 1_000_000, TestContext.Current.CancellationToken);
        var second = RouteSolver.Solve(problem, 0, new RouteBudget(1_000_000), 1_000_000, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Solve_NeverExceedsTheAllowance_AndStillReturnsAValidPath()
    {
        var problem = Lattice(Flat(40, 40, 1f), 1);
        foreach (var allowance in new long[] { 0, 1, 500, 20_000 })
        {
            var budget = new RouteBudget(RouteBudget.MaxEvaluations);
            var order = RouteSolver.Solve(problem, 3, budget, allowance, TestContext.Current.CancellationToken);
            AssertPermutation(order, problem.Count, 3);
            Assert.True(budget.Used <= allowance, $"used {budget.Used} of {allowance}");
        }
    }

    [Fact]
    public void Solve_GoesThroughTheGap_InsteadOfOverTheWall()
    {
        // Two rows of nodes at z = 0 either side of a 10 mm high wall along X with a gap at its
        // right end; crossing the wall costs 20 in Z, the detour through the gap costs 12 in XY.
        const int width = 20;
        const int height = 3;
        var floors = new float[width * height];
        for (var i = 0; i < width - 2; i++)
        {
            floors[1 * width + i] = 10f;
        }

        var grid = new RouteGrid(floors, width, height, 0, 0, 1f);
        var xs = new List<float>();
        var ys = new List<float>();
        for (var i = 0; i < width; i += 2)
        {
            xs.Add(i + 0.5f);
            ys.Add(0.5f);
            xs.Add(i + 0.5f);
            ys.Add(2.5f);
        }

        var problem = new RouteProblem(grid, xs.ToArray(), ys.ToArray(), new float[xs.Count]);
        var order = RouteSolver.Solve(problem, 0, new RouteBudget(1_000_000), 1_000_000, TestContext.Current.CancellationToken);
        var cost = RouteSolver.PathCost(problem, order);
        // Row 0 left to right (18 mm), through the gap (2 mm), row 2 right to left (18 mm).
        Assert.InRange(cost, 38 / RouteCost.XySpeedFactor, 1.1f * 38 / RouteCost.XySpeedFactor);
        for (var k = 1; k < order.Length; k++)
        {
            var climb = SurfacePath.Trace(grid, problem.Node(order[k - 1]), problem.Node(order[k]), null);
            Assert.Equal(0f, climb);
        }
    }

    [Fact]
    public void Solve_RespectsCancellation()
    {
        var problem = Lattice(Flat(100, 100, 1f), 1);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RouteSolver.Solve(problem, 0, new RouteBudget(1_000), 1_000, source.Token));
    }

    [Fact]
    public void Solve_EvaluationThroughput()
    {
        // Ten thousand lattice nodes on a rugged surface so that improving moves keep coming.
        var floors = new float[200 * 200];
        for (var k = 0; k < floors.Length; k++)
        {
            floors[k] = (k * 7919) % 13 * 0.1f;
        }

        var grid = new RouteGrid(floors, 200, 200, 0, 0, 0.5f);
        var problem = Lattice(grid, 2);
        var budget = new RouteBudget(RouteBudget.MaxEvaluations);
        var watch = Stopwatch.StartNew();
        var order = RouteSolver.Solve(problem, 0, budget, RouteBudget.MaxEvaluations, TestContext.Current.CancellationToken);
        watch.Stop();
        AssertPermutation(order, problem.Count, 0);
        Assert.True(budget.Used > 100_000, $"only {budget.Used} evaluations");
        var perSecond = budget.Used / watch.Elapsed.TotalSeconds;
        Assert.True(watch.Elapsed.TotalSeconds < 20, $"{budget.Used} evaluations took {watch.Elapsed.TotalSeconds:0.00} s ({perSecond:0} per second)");
        TestContext.Current.SendDiagnosticMessage($"solver: {problem.Count} nodes, {budget.Used} evaluations in {watch.Elapsed.TotalMilliseconds:0} ms, {perSecond / 1e6:0.0} M per second");
    }

    [Fact]
    public void Budget_SharesInProportionToNodes()
    {
        var budget = new RouteBudget(1000);
        Assert.Equal(250, budget.Share(25, 100));
        Assert.Equal(0, budget.Share(0, 100));
        Assert.Equal(0, budget.Share(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Share(5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouteBudget(-1));
    }

    [Fact]
    public void Solve_RejectsBadArguments()
    {
        var problem = Lattice(Flat(4, 4, 1f), 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => RouteSolver.Solve(problem, 16, new RouteBudget(0), 0, TestContext.Current.CancellationToken));
        var empty = new RouteProblem(Flat(1, 1, 1f), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>());
        Assert.Throws<ArgumentException>(() => RouteSolver.Solve(empty, 0, new RouteBudget(0), 0, TestContext.Current.CancellationToken));
        var single = new RouteProblem(Flat(1, 1, 1f), new[] { 0.5f }, new[] { 0.5f }, new[] { 0f });
        Assert.Equal(new[] { 0 }, RouteSolver.Solve(single, 0, new RouteBudget(0), 0, TestContext.Current.CancellationToken));
    }
}
