using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Solver;

namespace Miller.Core.Toolpaths.Strategies;

// Cave by cave, level by level. The level masks form a tree of caves (CaveTree); a cave is cut
// completely at its level along the fastest route through its nodes (NodeLattice at the Stepover,
// RouteSolver), then the tool drops one level into the first child cave, and it rises for the next
// sibling only when a whole subtree is done. Every route is solved over the material as it will
// stand at that moment: the cells of the cave at its level, everything else at what the previous
// routes left, so a move that leaves the cave climbs the standing material instead of cutting a
// slot through it. Between caves the writer chooses the surface polyline or a retract by time.
public sealed class ZLayerByLayerStrategy : IToolpathStrategy
{
    public const string StrategyId = "z-layer-by-layer";

    public string Id => StrategyId;

    public string DisplayName => "Z layer by layer";

    public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        var p = context.Parameters;
        var map = context.EffectiveTip;
        var steps = context.Plan.Steps;
        var tree = CaveTree.Build(steps);
        var step = NodeLattice.StepCells(p.Stepover, map.CellSize);
        var nodes = new Dictionary<Cave, List<int>>();
        var nodesLeft = 0L;
        foreach (var cave in All(tree.Roots))
        {
            var labels = tree.Labels[cave.Level];
            var id = cave.Id;
            var list = NodeLattice.Nodes((i, j) => labels[j * map.Width + i] == id, map.Width, map.Height, step);
            nodes[cave] = list;
            nodesLeft += list.Count;
        }

        var planned = context.Stock.Clone();
        var writer = new RouteWriter(p, context.SafeZ);
        var budget = new RouteBudget(RouteBudget.MaxEvaluations);
        var total = nodesLeft;
        var pending = new List<Cave>(tree.Roots);
        var stack = new Stack<List<Cave>>();
        stack.Push(pending);
        while (stack.Count > 0)
        {
            var siblings = stack.Peek();
            if (siblings.Count == 0)
            {
                stack.Pop();
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            var cave = Nearest(siblings, nodes, map, writer.Position);
            siblings.Remove(cave);
            var level = steps[cave.Level].Level;
            var caveNodes = nodes[cave];
            if (caveNodes.Count > 0)
            {
                var clearance = (float[])planned.Z.Clone();
                foreach (var c in cave.Cells)
                {
                    clearance[c] = level;
                }

                var grid = new RouteGrid(clearance, map.Width, map.Height, map.OriginX, map.OriginY, map.CellSize);
                var problem = Problem(grid, caveNodes, map, level);
                var start = NearestNode(problem, writer.Position);
                var order = RouteSolver.Solve(problem, start, budget, budget.Share(problem.Count, nodesLeft), cancellation);
                nodesLeft -= problem.Count;
                writer.Travel(problem.Node(order[0]), grid);
                for (var k = 1; k < order.Length; k++)
                {
                    writer.Follow(problem.Node(order[k]), grid);
                }

                progress?.Report(total == 0 ? 1f : (float)(total - nodesLeft) / total);
            }

            foreach (var c in cave.Cells)
            {
                planned.Z[c] = level;
            }

            stack.Push(new List<Cave>(cave.Children));
        }

        return writer.Finish();
    }

    // Nodes of a cave as a route problem, all at the level.
    public static RouteProblem Problem(RouteGrid grid, List<int> cells, HeightMap map, float level)
    {
        var xs = new float[cells.Count];
        var ys = new float[cells.Count];
        var zs = new float[cells.Count];
        for (var k = 0; k < cells.Count; k++)
        {
            var center = map.CellCenter(cells[k] % map.Width, cells[k] / map.Width);
            xs[k] = center.X;
            ys[k] = center.Y;
            zs[k] = level;
        }

        return new RouteProblem(grid, xs, ys, zs);
    }

    // The node nearest in XY to the tool, or the first node before the program starts.
    public static int NearestNode(RouteProblem problem, Vector3? position)
    {
        if (position is not Vector3 from)
        {
            return 0;
        }

        var best = 0;
        var bestDistance = float.PositiveInfinity;
        for (var k = 0; k < problem.Count; k++)
        {
            var dx = problem.X[k] - from.X;
            var dy = problem.Y[k] - from.Y;
            var d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }

        return best;
    }

    // The sibling whose nearest node is nearest in XY to the tool; the first one before the program
    // starts. A cave without nodes (its cells all lie inside a wider one at this level) still counts
    // for the standing material, so it is taken in list order.
    private static Cave Nearest(List<Cave> siblings, Dictionary<Cave, List<int>> nodes, HeightMap map, Vector3? position)
    {
        if (position is not Vector3 from)
        {
            return siblings[0];
        }

        var best = siblings[0];
        var bestDistance = float.PositiveInfinity;
        foreach (var cave in siblings)
        {
            foreach (var cell in nodes[cave])
            {
                var center = map.CellCenter(cell % map.Width, cell / map.Width);
                var dx = center.X - from.X;
                var dy = center.Y - from.Y;
                var d = dx * dx + dy * dy;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = cave;
                }
            }
        }

        return best;
    }

    private static IEnumerable<Cave> All(IEnumerable<Cave> caves)
    {
        foreach (var cave in caves)
        {
            yield return cave;
            foreach (var child in All(cave.Children))
            {
                yield return child;
            }
        }
    }
}
