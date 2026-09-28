using Miller.Solver;
using Xunit;

namespace Miller.Tests.Solver;

// The solver minimises travel plus turn fine: every move it applies lowers the fined cost of the
// route, so one more evaluation never makes the route worse; it cuts along two rows instead of
// zigzagging across them, fines every turn of a circle, and on a flat lattice runs straight rows.
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

    private static int FinedTurns(RouteProblem problem, int[] order) => Enumerable.Range(1, Math.Max(order.Length - 2, 0)).Count(k => TurnFine.IsFined(problem, order, k));

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

    // The fine change of a move is read at its joins only; dense lattices and curves, where every
    // node turns, must never raise the cost.
    [Fact]
    public void OneMoreEvaluation_NeverRaisesTheFinedCost_OnDenseLatticesAndCurves()
    {
        var random = new Random(139);
        var problems = new List<RouteProblem>();
        for (var trial = 0; trial < 2; trial++)
        {
            var seen = new HashSet<(float, float)>();
            var points = new List<(float, float)>();
            while (points.Count < 40)
            {
                var point = (random.Next(0, 12) * 0.2f, random.Next(0, 12) * 0.2f);
                if (seen.Add(point))
                {
                    points.Add(point);
                }
            }

            problems.Add(Points(0.2f, points.ToArray()));
        }

        var curve = new (float, float)[40];
        for (var k = 0; k < curve.Length; k++)
        {
            var radius = 3 + (k % 7) * 0.4;
            curve[k] = ((float)(radius * Math.Cos(k * 0.45)), (float)(radius * Math.Sin(k * 0.45)));
        }

        problems.Add(Points(0.2f, curve));
        foreach (var problem in problems)
        {
            var previous = RouteSolver.PathCost(problem, Solve(problem, 0, 0));
            for (var allowance = 1L; allowance <= 400; allowance++)
            {
                var cost = RouteSolver.PathCost(problem, Solve(problem, 0, allowance));
                Assert.True(cost <= previous + 1e-3f, $"allowance {allowance}: {cost} after {previous}");
                previous = cost;
            }
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
    public void CircularLoop_IsFinedAtEveryTurn()
    {
        // No three vertices of the octagon lie on a line, so every inner node of any order turns.
        var s = 3f;
        var problem = Points(0.2f, (0, 0), (s, 0), (2 * s, s), (2 * s, 2 * s), (s, 3 * s), (0, 3 * s), (-s, 2 * s), (-s, s));
        var order = Solve(problem, 0, 1_000_000);
        Assert.Equal(problem.Count - 2, FinedTurns(problem, order));
        Assert.True(TurnFine.SlowLength(problem, order) > 0f);
        Assert.Equal(TurnFine.SlowLength(problem, order) * TurnFine.PerSlowMillimetre, TurnFine.Fine(problem, order), 3);
    }

    [Fact]
    public void FlatLattice_RunsLongStraightRows()
    {
        // 12 x 8 nodes 3 mm apart. The row pattern turns twice at every row end (14 turns); a corner
        // rounded with two 45 degree turns on a lattice circle is fined now as well, so the solver
        // keeps to straight rows.
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
        var fined = FinedTurns(problem, order);
        Assert.True(fined <= FinedTurns(problem, rowOrder), $"{fined} fined turns");
        Assert.True(RouteSolver.PathCost(problem, order) <= RouteSolver.PathCost(problem, greedy) + 1e-3f);
        Assert.True(RouteSolver.PathCost(problem, order) <= RouteSolver.PathCost(problem, rowOrder) + 1e-3f);
    }

    [Fact]
    public void FinedSolve_IsDeterministic()
    {
        var problem = Lattice(15, 10, 1f);
        Assert.Equal(Solve(problem, 3, 2_000_000), Solve(problem, 3, 2_000_000));
    }
}
