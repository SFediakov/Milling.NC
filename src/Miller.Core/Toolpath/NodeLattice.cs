namespace Miller.Core.Toolpaths;

// The tool positions a route visits inside a region of cells: the cells on a square lattice spaced
// by the stepover (the first and the last grid line in each direction always count as lattice
// lines, so no strip at the far edge is skipped) plus the outline cells of the region, so that
// walls are cut where the region ends. Consecutive lattice nodes on a line are one stepover apart,
// which the cutter diameter covers; the outline nodes are one cell apart.
public static class NodeLattice
{
    // Guards floor() against 3 / 0.5 evaluating to 5.9999995.
    private const float StepEpsilon = 1e-4f;

    public static int StepCells(float spacing, float cellSize)
        => Math.Max(1, (int)MathF.Floor(spacing / cellSize + StepEpsilon));

    public static bool OnLattice(int index, int count, int step) => index % step == 0 || index == count - 1;

    // Flat indices (j * width + i) of the nodes of the region given by `inside`, in row-major order.
    // A cell is an outline cell when one of its eight neighbours lies in the grid but outside the
    // region.
    public static List<int> Nodes(Func<int, int, bool> inside, int width, int height, int step)
    {
        ArgumentNullException.ThrowIfNull(inside);
        if (step < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Lattice step must be at least one cell.");
        }

        var nodes = new List<int>();
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                if (!inside(i, j))
                {
                    continue;
                }

                if ((OnLattice(i, width, step) && OnLattice(j, height, step)) || IsOutline(inside, width, height, i, j))
                {
                    nodes.Add(j * width + i);
                }
            }
        }

        return nodes;
    }

    public static bool IsOutline(Func<int, int, bool> inside, int width, int height, int i, int j)
    {
        for (var dj = -1; dj <= 1; dj++)
        {
            for (var di = -1; di <= 1; di++)
            {
                var ii = i + di;
                var jj = j + dj;
                if ((di != 0 || dj != 0) && ii >= 0 && ii < width && jj >= 0 && jj < height && !inside(ii, jj))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
