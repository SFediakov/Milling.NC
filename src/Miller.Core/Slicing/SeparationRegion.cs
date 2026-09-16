using Miller.Core.Analysis;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;

namespace Miller.Core.Slicing;

// The plan restricted to the chosen cut scope, and what stands after it besides the model:
// Standing holds the stock surface over cells the roughing never touches, the terrace level over
// trench cells, and NaN where nothing but the model constrains the tool (model region, no stock).
public sealed record ScopedPlan(SlicePlan Plan, HeightMap Standing);

// Separation scope: the roughing removes only what frees the model from the stock. Per level the
// mask keeps the model region (cells whose tip stands above the floor: the tool must reach them to
// finish the model) plus the trench region of that level, built from the bottom up:
//   - the cells next to the obstacles of the level (tip above the level), one cell wide;
//   - everything cut at the level below, since material is removed layer by layer;
//   - the region of the shallowest deeper level whose tool head reaches into this slab (slab top
//     above that level plus CutterLength), widened by HeadRadius - CutterRadius + margin, so the
//     head working down there clears the trench wall.
// The trench is therefore a terrace whose steps are one cutter length apart.
public static class SeparationRegion
{
    // Cells adjacent to an obstacle, diagonals included, form the innermost trench.
    public static float AdjacencyMargin(float cellSize) => MathF.Sqrt(2) * cellSize;

    // Discretisation slack added to the head overhang: one diagonal cell plus the tolerance.
    public static float HeadMargin(float cellSize, float tolerance) => AdjacencyMargin(cellSize) + tolerance;

    // Everything scope: the plan as sliced, nothing standing besides the model.
    public static ScopedPlan Everything(SlicePlan plan, HeightMap grid)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(grid);
        return new ScopedPlan(plan, new HeightMap(grid.OriginX, grid.OriginY, grid.CellSize, grid.Width, grid.Height, float.NaN));
    }

    public static ScopedPlan Build(SlicePlan plan, HeightMap effectiveTip, HeightMap stock, ToolDefinition tool, CuttingParameters parameters, float stockTop, float floor)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!effectiveTip.SameGridAs(stock))
        {
            throw new ArgumentException("Tip map and stock map must share the same grid.", nameof(stock));
        }

        var steps = plan.RoughingSteps.ToList();
        var finishing = plan.FinishingStep ?? throw new ArgumentException("The plan has no finishing step.", nameof(plan));
        var width = effectiveTip.Width;
        var height = effectiveTip.Height;
        var cellSize = effectiveTip.CellSize;
        var modelRegion = ModelRegion(effectiveTip, floor);
        var hugRadius = AdjacencyMargin(cellSize) + ToolProfile.RadiusTolerance;
        var overhang = tool.HeadRadius - tool.CutterRadius + HeadMargin(cellSize, parameters.Tolerance);

        var deepest = new HeightMap(effectiveTip.OriginX, effectiveTip.OriginY, cellSize, width, height, float.NaN);
        var masks = new bool[steps.Count][,];
        for (var k = steps.Count - 1; k >= 0; k--)
        {
            var allowed = steps[k].Mask;
            var hug = Within(Obstacles(effectiveTip, steps[k].Level), cellSize, hugRadius);
            // Widening starts from every tool position of the deeper level, model region included.
            var below = k + 1 < steps.Count ? masks[k + 1] : null;
            var reach = HeadReach(steps, k, stockTop, tool.CutterLength);
            var cleared = reach >= 0 ? Within(masks[reach], cellSize, overhang) : null;
            var region = new bool[width, height];
            var mask = new bool[width, height];
            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    region[i, j] = allowed[i, j] && (hug[i, j] || (below is not null && below[i, j]) || (cleared is not null && cleared[i, j]));
                    if (region[i, j] && float.IsNaN(deepest[i, j]))
                    {
                        deepest[i, j] = steps[k].Level;
                    }

                    mask[i, j] = region[i, j] || (modelRegion[i, j] && allowed[i, j]);
                }
            }

            masks[k] = mask;
        }

        var standing = new HeightMap(effectiveTip.OriginX, effectiveTip.OriginY, cellSize, width, height, float.NaN);
        var finishingMask = new bool[width, height];
        var innermost = steps.Count > 0 ? masks[^1] : null;
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var tip = effectiveTip[i, j];
                var stockZ = stock[i, j];
                finishingMask[i, j] = finishing.Mask[i, j] && (modelRegion[i, j] || (innermost is not null && innermost[i, j]));
                if (float.IsNaN(tip) || float.IsNaN(stockZ) || modelRegion[i, j])
                {
                    continue;
                }

                standing[i, j] = float.IsNaN(deepest[i, j]) ? stockZ : deepest[i, j];
            }
        }

        var scoped = new List<MillingStep>(steps.Count + 1);
        for (var k = 0; k < steps.Count; k++)
        {
            scoped.Add(new MillingStep(steps[k].Level, MillingOperation.Roughing, masks[k]));
        }

        scoped.Add(new MillingStep(finishing.Level, MillingOperation.Finishing, finishingMask));
        return new ScopedPlan(new SlicePlan(scoped, plan.LowestLevel), standing);
    }

    // Cells the tool must visit to finish the model: the tip stands above the floor there.
    public static bool[,] ModelRegion(HeightMap effectiveTip, float floor)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var region = new bool[effectiveTip.Width, effectiveTip.Height];
        for (var j = 0; j < effectiveTip.Height; j++)
        {
            for (var i = 0; i < effectiveTip.Width; i++)
            {
                var z = effectiveTip[i, j];
                region[i, j] = !float.IsNaN(z) && z > floor + FinalModelAnalyzer.FloorTolerance;
            }
        }

        return region;
    }

    // Index of the shallowest deeper step whose tool head reaches into slab k (the slab top lies
    // above that level plus the cutter length), or -1. Slab k spans from the level above (the
    // stock top for the first step) down to its own level.
    public static int HeadReach(IReadOnlyList<MillingStep> steps, int k, float stockTop, float cutterLength)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var slabTop = k == 0 ? stockTop : steps[k - 1].Level;
        for (var m = k + 1; m < steps.Count; m++)
        {
            if (slabTop > steps[m].Level + cutterLength + Slicer.LevelTolerance)
            {
                return m;
            }
        }

        return -1;
    }

    // Cells whose tip stands above the level.
    public static bool[,] Obstacles(HeightMap effectiveTip, float level)
    {
        var obstacles = new bool[effectiveTip.Width, effectiveTip.Height];
        for (var j = 0; j < effectiveTip.Height; j++)
        {
            for (var i = 0; i < effectiveTip.Width; i++)
            {
                var z = effectiveTip[i, j];
                obstacles[i, j] = !float.IsNaN(z) && z > level;
            }
        }

        return obstacles;
    }

    // Cells within the radius of a marked cell (the marked cells included).
    public static bool[,] Within(bool[,] marked, float cellSize, float radius)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var distance = DistanceTransform.Distances(marked, cellSize);
        var width = marked.GetLength(0);
        var height = marked.GetLength(1);
        var result = new bool[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                result[i, j] = distance[i, j] <= radius;
            }
        }

        return result;
    }
}
