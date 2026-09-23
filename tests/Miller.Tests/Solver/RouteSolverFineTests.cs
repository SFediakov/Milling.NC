using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// The solver minimises travel plus turn fine: every move it applies lowers the fined cost of the
// route, so one more evaluation never makes the route worse; it cuts along two rows instead of
// zigzagging across them, keeps a circular loop, and on a flat lattice turns along lattice arcs
// rather than at right angles.
public sealed class RouteSolverFineTests
{
    private static RouteProblem Points(float cell, params (float X, float Y)[] points)
        => new(new RouteGrid(new float[1], 1, 1, 0, 0, cell), points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(), new float[points.Length]);

    private static RouteProblem Lattice(int columns, int rows, float spacing)
    {
        var points = new List<(float, float)>();
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                points.Add((i * spacing, j * spacing));
            }
        }

        return Points(0.5f, points.ToArray());
    }

    private static int FinedTurns(RouteProblem problem, int[] order) => Turns(problem, order, fined: true);

    // Turns above 35 degrees that are fined, or that are exempt as circular.
    private static int Turns(RouteProblem problem, int[] order, bool fined)
    {
        var sharp = MathF.Cos(TurnFine.SharpTurnDegrees * MathF.PI / 180f);
        var count = 0;
        for (var k = 1; k < order.Length - 1; k++)
        {
            var isFined = TurnFine.IsFined(problem, k >= 2 ? order[k - 2] : -1, order[k - 1], order[k], order[k + 1], k + 2 < order.Length ? order[k + 2] : -1);
            var ux = problem.X[order[k]] - problem.X[order[k - 1]];
            var uy = problem.Y[order[k]] - problem.Y[order[k - 1]];
            var wx = problem.X[order[k + 1]] - problem.X[order[k]];
            var wy = problem.Y[order[k + 1]] - problem.Y[order[k]];
            var cos = (ux * wx + uy * wy) / MathF.Sqrt((ux * ux + uy * uy) * (wx * wx + wy * wy));
            if (fined ? isFined : !isFined && cos < sharp)
            {
                count++;
            }
        }

        return count;
    }

    private static int[] Solve(RouteProblem problem, int start, long allowance)
        => RouteSolver.Solve(problem, start, new RouteBudget(RouteBudget.MaxEvaluations), allowance, TestContext.Current.CancellationToken);

    [Fact]
    public void OneMoreEvaluation_NeverRaisesTheFinedCost()
    {
        // Each applied move is accepted on its computed gain; a wrong fine delta would let a move
        // through that raises the recomputed cost. Random points and a rugged lattice.
        var random = new Random(4242);
        var problems = new List<RouteProblem>();
        for (var trial = 0; trial < 3; trial++)
        {
            var points = new (float, float)[30];
            for (var k = 0; k < points.Length; k++)
            {
                points[k] = ((float)(random.NextDouble() * 25), (float)(random.NextDouble() * 25));
            }

            problems.Add(Points(0.5f, points));
        }

        var floors = new float[30 * 30];
        for (var k = 0; k < floors.Length; k++)
        {
            floors[k] = (k * 7919) % 13 * 0.3f;
        }

        var grid = new RouteGrid(floors, 30, 30, 0, 0, 0.5f);
        var xs = new List<float>();
        var ys = new List<float>();
        for (var j = 0; j < 30; j += 3)
        {
            for (var i = 0; i < 30; i += 3)
            {
                xs.Add((i + 0.5f) * 0.5f);
                ys.Add((j + 0.5f) * 0.5f);
            }
        }

        problems.Add(new RouteProblem(grid, xs.ToArray(), ys.ToArray(), new float[xs.Count]));
        foreach (var problem in problems)
        {
            var previous = RouteSolver.PathCost(problem, Solve(problem, 0, 0));
            var improved = false;
            for (var allowance = 1L; allowance <= 600; allowance++)
            {
                var cost = RouteSolver.PathCost(problem, Solve(problem, 0, allowance));
                Assert.True(cost <= previous + 1e-3f, $"allowance {allowance}: {cost} after {previous}");
                improved |= cost < previous - 1e-3f;
                previous = cost;
            }

            Assert.True(improved);
        }
    }

    [Fact]
    public void TwoRows_AreCutAlong_InsteadOfZigzaggingAcross()
    {
        // Two rows 1 mm apart with nodes every 2 mm: zigzagging across is 9 mm shorter than going
        // along one row and back the other, but it turns 90 degrees at every node.
        var points = new List<(float, float)>();
        for (var i = 0; i < 10; i++)
        {
            points.Add((2f * i, 0f));
            points.Add((2f * i, 1f));
        }

        var problem = Points(0.1f, points.ToArray());
        var order = Solve(problem, 0, 1_000_000);
        var along = Enumerable.Range(0, 10).Select(i => 2 * i).Concat(Enumerable.Range(0, 10).Select(i => 2 * (9 - i) + 1)).ToArray();
        var across = Enumerable.Range(0, 10).SelectMany(i => i % 2 == 0 ? new[] { 2 * i, 2 * i + 1 } : new[] { 2 * i + 1, 2 * i }).ToArray();
        float Travel(int[] o) => Enumerable.Range(1, o.Length - 1).Sum(k => RouteCost.Exact(problem.Grid, problem.Node(o[k - 1]), problem.Node(o[k])));
        Assert.True(Travel(across) < Travel(along));
        Assert.True(RouteSolver.PathCost(problem, along) < RouteSolver.PathCost(problem, across));
        Assert.True(RouteSolver.PathCost(problem, order) <= RouteSolver.PathCost(problem, along) + 1e-3f,
            $"solved {RouteSolver.PathCost(problem, order)}, along {RouteSolver.PathCost(problem, along)}");
        Assert.True(FinedTurns(problem, order) <= FinedTurns(problem, along), $"{FinedTurns(problem, order)} fined turns");
    }

    [Fact]
    public void CircularLoop_IsKept_WithoutFine()
    {
        var s = 3f;
        var problem = Points(0.2f, (0, 0), (s, 0), (2 * s, s), (2 * s, 2 * s), (s, 3 * s), (0, 3 * s), (-s, 2 * s), (-s, s));
        var order = Solve(problem, 0, 1_000_000);
        Assert.Equal(0, FinedTurns(problem, order));
        Assert.Equal(0f, TurnFine.Fine(problem, order));
    }

    [Fact]
    public void FlatLattice_TurnsAlongArcs_WithFewerFinedTurnsThanTheRowPattern()
    {
        // 12 x 8 nodes 3 mm apart. The row pattern turns 90 degrees twice at every row end; the
        // solver rounds its corners with two 45 degree turns on one lattice circle, which are exempt.
        var problem = Lattice(12, 8, 3f);
        var greedy = Solve(problem, 0, 0);
        var order = Solve(problem, 0, RouteBudget.MaxEvaluations);
        Assert.Equal(problem.Count, order.Distinct().Count());
        var rows = new List<int>();
        for (var j = 0; j < 8; j++)
        {
            for (var i = 0; i < 12; i++)
            {
                rows.Add(j * 12 + (j % 2 == 0 ? i : 11 - i));
            }
        }

        var rowOrder = rows.ToArray();
        Assert.Equal(14, FinedTurns(problem, rowOrder));
        Assert.Equal(0, Turns(problem, rowOrder, fined: false));
        var fined = FinedTurns(problem, order);
        var circular = Turns(problem, order, fined: false);
        Assert.True(fined < FinedTurns(problem, rowOrder), $"{fined} fined turns");
        Assert.True(circular > fined, $"{circular} circular, {fined} fined turns");
        Assert.True(RouteSolver.PathCost(problem, order) < RouteSolver.PathCost(problem, greedy));
    }

    [Fact]
    public void FinedSolve_IsDeterministic()
    {
        var problem = Lattice(15, 10, 1f);
        Assert.Equal(Solve(problem, 3, 2_000_000), Solve(problem, 3, 2_000_000));
    }
}
