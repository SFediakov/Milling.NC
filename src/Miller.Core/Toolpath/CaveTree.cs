using Miller.Core.Slicing;

namespace Miller.Core.Toolpaths;

// One cave: an 8-connected component of the mask of one level, with the components of the next
// level that lie inside it. Cells are flat indices (j * width + i).
public sealed class Cave
{
    public Cave(int level, int id, int[] cells)
    {
        Level = level;
        Id = id;
        Cells = cells;
    }

    // Index of the step in the plan.
    public int Level { get; }

    // Label of the component within its level.
    public int Id { get; }

    public int[] Cells { get; }

    public List<Cave> Children { get; } = new();
}

// The level masks of a plan as a forest of caves. The masks are nested (a lower level is reachable
// only where the level above is), so every component of a level lies inside one component of the
// level above and becomes its child; a component with no cell in the level above starts a root.
// Cutting a cave completely at its level, then its children one by one, and only then rising
// for the next sibling is the order the layer strategy follows.
public sealed class CaveTree
{
    private CaveTree(IReadOnlyList<Cave> roots, int[][] labels, int width, int height)
    {
        Roots = roots;
        Labels = labels;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<Cave> Roots { get; }

    // Per level, the component label of every cell or -1 outside the mask.
    public int[][] Labels { get; }

    public int Width { get; }

    public int Height { get; }

    public static CaveTree Build(IReadOnlyList<MillingStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return new CaveTree(Array.Empty<Cave>(), Array.Empty<int[]>(), 0, 0);
        }

        var width = steps[0].Width;
        var height = steps[0].Height;
        var labels = new int[steps.Count][];
        var caves = new List<Cave>[steps.Count];
        var roots = new List<Cave>();
        for (var k = 0; k < steps.Count; k++)
        {
            if (steps[k].Width != width || steps[k].Height != height)
            {
                throw new ArgumentException("Every step must share the same grid.", nameof(steps));
            }

            labels[k] = Label(steps[k].Mask, width, height, out var components);
            caves[k] = new List<Cave>(components.Count);
            for (var id = 0; id < components.Count; id++)
            {
                var cave = new Cave(k, id, components[id]);
                caves[k].Add(cave);
                var parent = k > 0 ? labels[k - 1][cave.Cells[0]] : -1;
                if (parent >= 0)
                {
                    caves[k - 1][parent].Children.Add(cave);
                }
                else
                {
                    roots.Add(cave);
                }
            }
        }

        return new CaveTree(roots, labels, width, height);
    }

    // 8-connected components of the mask, in order of their first cell in row-major scan.
    public static int[] Label(bool[,] mask, int width, int height, out List<int[]> components)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var labels = new int[width * height];
        Array.Fill(labels, -1);
        components = new List<int[]>();
        var queue = new Queue<int>();
        var cells = new List<int>();
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var seed = j * width + i;
                if (!mask[i, j] || labels[seed] >= 0)
                {
                    continue;
                }

                var id = components.Count;
                labels[seed] = id;
                queue.Enqueue(seed);
                cells.Clear();
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    cells.Add(c);
                    var ci = c % width;
                    var cj = c / width;
                    for (var dj = -1; dj <= 1; dj++)
                    {
                        for (var di = -1; di <= 1; di++)
                        {
                            var ii = ci + di;
                            var jj = cj + dj;
                            if (ii < 0 || ii >= width || jj < 0 || jj >= height || !mask[ii, jj])
                            {
                                continue;
                            }

                            var n = jj * width + ii;
                            if (labels[n] < 0)
                            {
                                labels[n] = id;
                                queue.Enqueue(n);
                            }
                        }
                    }
                }

                components.Add(cells.ToArray());
            }
        }

        return labels;
    }
}
