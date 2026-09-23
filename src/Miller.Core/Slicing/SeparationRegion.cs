using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Setup;

namespace Miller.Core.Slicing;

// The plan restricted to the chosen cut scope, and what stands after it besides the model:
// Standing holds the stock surface over cells the levels never touch, the terrace level over
// trench cells, and NaN where nothing but the model constrains the tool (model region, no stock,
// islands milled out). MilledIslands lists the islands below the volume to keep.
public sealed record ScopedPlan(SlicePlan Plan, HeightMap Standing, IReadOnlyList<MaterialIsland> MilledIslands);

// Separation scope (native mn_separation): the levels remove only what frees the model from the
// stock. Per level the mask keeps the model region (cells whose tip stands above the floor: the tool
// must reach them to finish the model) plus the trench region of that level, built from the bottom up:
//   - the cells next to the obstacles of the level (tip above the level), one cell wide;
//   - everything cut at the level below, since material is removed layer by layer;
//   - the region of the shallowest deeper level whose tool head reaches into this slab (slab top
//     above that level plus CutterLength), widened by HeadRadius - CutterRadius + margin (the widest
//     head radius for a frustum), so the head working down there clears the trench wall.
// The trench is therefore a terrace whose steps are one cutter length apart. Islands of standing
// stock the trench encloses (MaterialIslands) whose volume is below minIslandVolume are milled
// out like the unrestricted plan would.
public static class SeparationRegion
{
    // Cells adjacent to an obstacle, diagonals included, form the innermost trench.
    public static float AdjacencyMargin(float cellSize) => CoreNative.mn_adjacency_margin(cellSize);

    // Discretisation slack added to the head overhang: one diagonal cell plus the tolerance.
    public static float HeadMargin(float cellSize, float tolerance) => CoreNative.mn_head_margin(cellSize, tolerance);

    // Everything scope: the plan as sliced, nothing standing besides the model.
    public static ScopedPlan Everything(SlicePlan plan, HeightMap grid)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(grid);
        return new ScopedPlan(plan, CoreNative.Empty(grid, float.NaN), Array.Empty<MaterialIsland>());
    }

    public static unsafe ScopedPlan Build(SlicePlan plan, HeightMap effectiveTip, HeightMap stock, ToolDefinition tool, CuttingParameters parameters, float stockTop, float floor, float minIslandVolume)
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

        using var native = new CoreNative.NativePlan(plan, effectiveTip);
        var standing = CoreNative.Empty(effectiveTip, float.NaN);
        var nativeTool = CoreNative.ToolOf(tool);
        int* cells = null;
        int* offsets = null;
        float* volumes = null;
        int count;
        fixed (float* t = effectiveTip.Z, s = stock.Z, st = standing.Z)
        {
            CoreNative.Check(CoreNative.mn_separation(native.Handle, t, s, &nativeTool, parameters.Tolerance, stockTop, floor, minIslandVolume, st, &cells, &offsets, &volumes, &count));
        }

        try
        {
            return new ScopedPlan(CoreNative.PlanOf(native.Handle, effectiveTip.Width, effectiveTip.Height), standing, MaterialIslands.IslandsOf(cells, offsets, volumes, count));
        }
        finally
        {
            CoreNative.mn_free(cells);
            CoreNative.mn_free(offsets);
            CoreNative.mn_free(volumes);
        }
    }

    // Cells the tool must visit to finish the model: the tip stands above the floor there.
    public static unsafe bool[,] ModelRegion(HeightMap effectiveTip, float floor)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var region = new byte[effectiveTip.CellCount];
        fixed (float* t = effectiveTip.Z)
        fixed (byte* r = region)
        {
            CoreNative.mn_model_region(t, effectiveTip.CellCount, floor, r);
        }

        return CoreNative.Mask(region, effectiveTip.Width, effectiveTip.Height);
    }

    // Index of the shallowest deeper step whose tool head reaches into slab k (the slab top lies
    // above that level plus the cutter length), or -1. Slab k spans from the level above (the
    // stock top for the first step) down to its own level.
    public static unsafe int HeadReach(IReadOnlyList<MillingStep> steps, int k, float stockTop, float cutterLength)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var levels = steps.Select(s => s.Level).ToArray();
        fixed (float* l = levels)
        {
            return CoreNative.mn_head_reach(l, levels.Length, k, stockTop, cutterLength);
        }
    }

    // Cells whose tip stands above the level.
    public static unsafe bool[,] Obstacles(HeightMap effectiveTip, float level)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var obstacles = new byte[effectiveTip.CellCount];
        fixed (float* t = effectiveTip.Z)
        fixed (byte* o = obstacles)
        {
            CoreNative.mn_obstacles(t, effectiveTip.CellCount, level, o);
        }

        return CoreNative.Mask(obstacles, effectiveTip.Width, effectiveTip.Height);
    }

    // Cells within the radius of a marked cell (the marked cells included).
    public static unsafe bool[,] Within(bool[,] marked, float cellSize, float radius)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var width = marked.GetLength(0);
        var height = marked.GetLength(1);
        var bytes = CoreNative.Bytes(marked);
        var result = new byte[Math.Max(bytes.Length, 1)];
        fixed (byte* m = bytes, r = result)
        {
            CoreNative.Check(CoreNative.mn_within(m, width, height, cellSize, radius, r));
        }

        return CoreNative.Mask(result, width, height);
    }
}
