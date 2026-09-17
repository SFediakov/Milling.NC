using Miller.Core.HeightMaps;
using Miller.Solver;

namespace Miller.Core.Toolpaths.Strategies;

// One route over the whole surface with free 3-axis moves. The nodes are the coverage cells on the
// FinishingStepover lattice plus every cell where the effective tip steps by more than the
// tolerance to a neighbour (walls and edges), each at its effective tip height; the route is the
// fastest the solver finds and every move between nodes follows the surface polyline, so the tool
// is never under the effective tip. Stepdown plays no part: a wall is cut at full depth.
public sealed class ThreeAxisPreciseStrategy : IToolpathStrategy
{
    public const string StrategyId = "three-axis-precise";

    public string Id => StrategyId;

    public string DisplayName => "3 axis precise";

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var coverage = context.Plan.Coverage;
        var step = NodeLattice.StepCells(p.FinishingStepover, map.CellSize);
        bool Inside(int i, int j) => coverage[i, j] && !float.IsNaN(map[i, j]);
        var cells = NodeLattice.Nodes(Inside, map.Width, map.Height, step);
        var marked = new bool[map.CellCount];
        foreach (var c in cells)
        {
            marked[c] = true;
        }

        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var c = j * map.Width + i;
                if (!marked[c] && Inside(i, j) && IsStep(map, i, j, p.Tolerance))
                {
                    marked[c] = true;
                    cells.Add(c);
                }
            }
        }

        var writer = new RouteWriter(p, context.SafeZ);
        if (cells.Count == 0)
        {
            return writer.Finish();
        }

        cells.Sort();
        var grid = new RouteGrid(map.Z, map.Width, map.Height, map.OriginX, map.OriginY, map.CellSize);
        var xs = new float[cells.Count];
        var ys = new float[cells.Count];
        var zs = new float[cells.Count];
        for (var k = 0; k < cells.Count; k++)
        {
            var center = map.CellCenter(cells[k] % map.Width, cells[k] / map.Width);
            xs[k] = center.X;
            ys[k] = center.Y;
            zs[k] = map.Z[cells[k]];
        }

        var problem = new RouteProblem(grid, xs, ys, zs);
        var budget = new RouteBudget(RouteBudget.MaxEvaluations);
        var order = RouteSolver.Solve(problem, 0, budget, budget.Share(problem.Count, problem.Count), cancellation);
        progress?.Report(0.5f);
        writer.Travel(problem.Node(order[0]), grid);
        for (var k = 1; k < order.Length; k++)
        {
            writer.Follow(problem.Node(order[k]), grid);
        }

        progress?.Report(1f);
        return writer.Finish();
    }

    // True where a neighbour's tip differs by more than the tolerance or holds no material.
    public static bool IsStep(HeightMap map, int i, int j, float tolerance)
    {
        var z = map[i, j];
        for (var dj = -1; dj <= 1; dj++)
        {
            for (var di = -1; di <= 1; di++)
            {
                var ii = i + di;
                var jj = j + dj;
                if ((di == 0 && dj == 0) || !map.InBounds(ii, jj))
                {
                    continue;
                }

                var n = map[ii, jj];
                if (float.IsNaN(n) || MathF.Abs(n - z) > tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
