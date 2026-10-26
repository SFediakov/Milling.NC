using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// T-166: a route can be made to finish at a given node. The start walks put it last and no 2-opt or
// Or-opt move takes it from there; the route still visits every node once from the start.
public sealed class RouteSolverFixedEndTests
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

    private static int[] Solve(RouteProblem problem, int start, int end)
        => RouteSolver.Solve(problem, start, new RouteBudget(2_000_000), 2_000_000, TestContext.Current.CancellationToken, end);

    [Theory]
    [InlineData(0, 112)]
    [InlineData(0, 1)]
    [InlineData(56, 0)]
    [InlineData(224, 112)]
    public void FixedEnd_IsTheLastNode_AndEveryNodeIsVisitedOnce(int start, int end)
    {
        var grid = Flat(30, 30, 1f);
        var (xs, ys) = Lattice(grid, 2);
        var problem = new RouteProblem(grid, xs, ys, new float[xs.Length]);
        var order = Solve(problem, start, end);
        Assert.Equal(xs.Length, order.Length);
        Assert.Equal(xs.Length, order.Distinct().Count());
        Assert.Equal(start, order[0]);
        Assert.Equal(end, order[^1]);
    }

    // The free route's own last node as the fixed end: the solver may still find that route, so the
    // fixed one is never longer than the free one by more than the local search's slack, and it is a
    // proper route of the same nodes.
    [Fact]
    public void FreeRoutesEnd_AsTheFixedEnd_CostsAboutTheSame()
    {
        var grid = Flat(30, 30, 1f);
        var (xs, ys) = Lattice(grid, 2);
        var problem = new RouteProblem(grid, xs, ys, new float[xs.Length]);
        var free = Solve(problem, 0, RouteSolver.OpenEnd);
        var fixedEnd = Solve(problem, 0, free[^1]);
        Assert.Equal(free[^1], fixedEnd[^1]);
        var freeCost = RouteSolver.PathCost(problem, free);
        var fixedCost = RouteSolver.PathCost(problem, fixedEnd);
        Assert.True(fixedCost <= freeCost * 1.05f, $"fixed {fixedCost} against free {freeCost}");
    }

    [Fact]
    public void TwoNodes_RunFromTheStartToTheEnd()
    {
        var grid = Flat(10, 10, 1f);
        var problem = new RouteProblem(grid, new[] { 1.5f, 7.5f }, new[] { 1.5f, 7.5f }, new float[2]);
        Assert.Equal(new[] { 1, 0 }, Solve(problem, 1, 0));
    }

    [Fact]
    public void AnEndEqualToTheStart_OrOutsideTheNodes_IsRejected()
    {
        var grid = Flat(30, 30, 1f);
        var (xs, ys) = Lattice(grid, 2);
        var problem = new RouteProblem(grid, xs, ys, new float[xs.Length]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Solve(problem, 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Solve(problem, 3, xs.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => Solve(problem, 3, -2));
    }
}
