using Miller.Core.HeightMaps;
using Miller.Core.Slicing;
using Miller.Solver;

namespace Miller.Core.Toolpaths.Strategies;

// Free 3-axis moves over the surface, one stepdown at a time. The levels are the plan's
// (stock top minus k x Stepdown, the last one at the lowest tip). At level L the nodes are the
// coverage cells whose effective tip lies below the previous level (they still carry material),
// on the FinishingStepover lattice plus every cell where the level map max(tip, L) steps by more
// than the tolerance to a neighbour (walls and edges), each at max(tip, L). One route per level is
// the fastest the solver finds and every move follows the surface polyline over the level map, so
// the tool is never under the effective tip and never deeper than one Stepdown into the material
// the previous level left. The last visit of a cell is at its tip, so the surface is followed
// without level quantization where it lies between two levels.
public sealed class ThreeAxisFreedomStrategy : IToolpathStrategy
{
    public const string StrategyId = "three-axis-freedom";

    // Id of the same strategy before the one-stepdown rule; project files still naming it load.
    public const string LegacyStrategyId = "three-axis-precise";

    public string Id => StrategyId;

    public string DisplayName => "3 axis freedom";

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var coverage = context.Plan.Coverage;
        var stock = context.Stock;
        var step = NodeLattice.StepCells(p.FinishingStepover, map.CellSize);
        var writer = new RouteWriter(p, context.SafeZ);
        var levels = context.Plan.Steps.Select(s => s.Level).ToList();
        var passes = new List<(List<int> Cells, HeightMap LevelMap)>();
        var nodesLeft = 0L;
        var above = context.StockTop;
        foreach (var level in levels)
        {
            var previous = above;
            bool Inside(int i, int j)
            {
                var k = j * map.Width + i;
                var tip = map.Z[k];
                return coverage[i, j] && !float.IsNaN(tip) && tip < previous - Slicer.LevelTolerance && stock.Z[k] > level + Slicer.LevelTolerance;
            }

            var levelMap = LevelMap(map, level);
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
                    if (!marked[c] && Inside(i, j) && IsStep(levelMap, i, j, p.Tolerance))
                    {
                        marked[c] = true;
                        cells.Add(c);
                    }
                }
            }

            cells.Sort();
            passes.Add((cells, levelMap));
            nodesLeft += cells.Count;
            above = level;
        }

        if (nodesLeft == 0)
        {
            return writer.Finish();
        }

        var budget = new RouteBudget(RouteBudget.MaxEvaluations);
        var total = nodesLeft;
        foreach (var (cells, levelMap) in passes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cells.Count == 0)
            {
                continue;
            }

            var grid = new RouteGrid(levelMap.Z, map.Width, map.Height, map.OriginX, map.OriginY, map.CellSize);
            var xs = new float[cells.Count];
            var ys = new float[cells.Count];
            var zs = new float[cells.Count];
            for (var k = 0; k < cells.Count; k++)
            {
                var center = map.CellCenter(cells[k] % map.Width, cells[k] / map.Width);
                xs[k] = center.X;
                ys[k] = center.Y;
                zs[k] = levelMap.Z[cells[k]];
            }

            var problem = new RouteProblem(grid, xs, ys, zs);
            var start = ZLayerByLayerStrategy.NearestNode(problem, writer.Position);
            var order = RouteSolver.Solve(problem, start, budget, budget.Share(problem.Count, nodesLeft), cancellation);
            nodesLeft -= problem.Count;
            writer.Travel(problem.Node(order[0]), grid);
            for (var k = 1; k < order.Length; k++)
            {
                writer.Follow(problem.Node(order[k]), grid);
            }

            progress?.Report((float)(total - nodesLeft) / total);
        }

        return writer.Finish();
    }

    // The surface as it stands once the level is cut: the effective tip where it is above the level,
    // the level elsewhere; NaN stays NaN.
    public static HeightMap LevelMap(HeightMap tip, float level)
    {
        var result = tip.Clone();
        for (var k = 0; k < result.Z.Length; k++)
        {
            var z = result.Z[k];
            if (!float.IsNaN(z) && z < level)
            {
                result.Z[k] = level;
            }
        }

        return result;
    }

    // True where a neighbour's height differs by more than the tolerance or holds no material.
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
