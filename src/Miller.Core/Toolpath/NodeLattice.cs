using Miller.Core.Native;

namespace Miller.Core.Toolpaths;

// The tool positions a route visits inside a region of cells (native mn_lattice_nodes): the cells on
// a square lattice spaced by the stepover (the first and the last grid line in each direction always
// count as lattice lines, so no strip at the far edge is skipped) plus the outline cells of the
// region, so that walls are cut where the region ends. Consecutive lattice nodes on a line are one
// stepover apart, which the cutter diameter covers; the outline nodes are one cell apart.
public static class NodeLattice
{
    public static int StepCells(float spacing, float cellSize) => CoreNative.mn_step_cells(spacing, cellSize);

    public static bool OnLattice(int index, int count, int step) => CoreNative.mn_on_lattice(index, count, step) != 0;

    // Flat indices (j * width + i) of the nodes of the region given by `inside`, in row-major order.
    // A cell is an outline cell when one of its eight neighbours lies in the grid but outside the
    // region.
    public static unsafe List<int> Nodes(Func<int, int, bool> inside, int width, int height, int step)
    {
        ArgumentNullException.ThrowIfNull(inside);
        if (step < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Lattice step must be at least one cell.");
        }

        var mask = Evaluate(inside, width, height);
        int* nodes = null;
        int count;
        fixed (byte* m = mask)
        {
            CoreNative.Check(CoreNative.mn_lattice_nodes(m, width, height, step, &nodes, &count));
        }

        try
        {
            return new List<int>(new ReadOnlySpan<int>(nodes, count).ToArray());
        }
        finally
        {
            CoreNative.mn_free(nodes);
        }
    }

    public static unsafe bool IsOutline(Func<int, int, bool> inside, int width, int height, int i, int j)
    {
        ArgumentNullException.ThrowIfNull(inside);
        var mask = Evaluate(inside, width, height);
        fixed (byte* m = mask)
        {
            return CoreNative.mn_is_outline(m, width, height, i, j) != 0;
        }
    }

    private static byte[] Evaluate(Func<int, int, bool> inside, int width, int height)
    {
        var mask = new byte[Math.Max(width * height, 1)];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                mask[j * width + i] = inside(i, j) ? (byte)1 : (byte)0;
            }
        }

        return mask;
    }
}
