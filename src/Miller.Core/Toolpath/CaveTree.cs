using Miller.Core.Native;
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

// The level masks of a plan as a forest of caves (native mn_caves). The masks are nested (a lower
// level is reachable only where the level above is), so every component of a level lies inside one
// component of the level above and becomes its child; a component with no cell in the level above
// starts a root. Cutting a cave completely at its level, then its children one by one, and only then
// rising for the next sibling is the order the layer strategy follows.
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

    public static unsafe CaveTree Build(IReadOnlyList<MillingStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return new CaveTree(Array.Empty<Cave>(), Array.Empty<int[]>(), 0, 0);
        }

        var width = steps[0].Width;
        var height = steps[0].Height;
        var cells = width * height;
        var masks = new byte[steps.Count * cells];
        for (var k = 0; k < steps.Count; k++)
        {
            if (steps[k].Width != width || steps[k].Height != height)
            {
                throw new ArgumentException("Every step must share the same grid.", nameof(steps));
            }

            CoreNative.Bytes(steps[k].Mask).CopyTo(masks, k * cells);
        }

        var labels = new int[steps.Count * cells];
        int* levels = null, ids = null, caveCells = null, cellOffsets = null, children = null, childOffsets = null, roots = null;
        int caveCount, rootCount;
        fixed (byte* m = masks)
        fixed (int* l = labels)
        {
            CoreNative.Check(CoreNative.mn_caves(m, steps.Count, width, height, l, &levels, &ids, &caveCells, &cellOffsets, &children, &childOffsets, &roots, &caveCount, &rootCount));
        }

        try
        {
            var caves = new Cave[caveCount];
            for (var c = 0; c < caveCount; c++)
            {
                caves[c] = new Cave(levels[c], ids[c], new ReadOnlySpan<int>(caveCells + cellOffsets[c], cellOffsets[c + 1] - cellOffsets[c]).ToArray());
            }

            for (var c = 0; c < caveCount; c++)
            {
                for (var m = childOffsets[c]; m < childOffsets[c + 1]; m++)
                {
                    caves[c].Children.Add(caves[children[m]]);
                }
            }

            var rootList = new List<Cave>(rootCount);
            for (var r = 0; r < rootCount; r++)
            {
                rootList.Add(caves[roots[r]]);
            }

            var perLevel = new int[steps.Count][];
            for (var k = 0; k < steps.Count; k++)
            {
                perLevel[k] = labels.AsSpan(k * cells, cells).ToArray();
            }

            return new CaveTree(rootList, perLevel, width, height);
        }
        finally
        {
            CoreNative.mn_free(levels);
            CoreNative.mn_free(ids);
            CoreNative.mn_free(caveCells);
            CoreNative.mn_free(cellOffsets);
            CoreNative.mn_free(children);
            CoreNative.mn_free(childOffsets);
            CoreNative.mn_free(roots);
        }
    }

    // 8-connected components of the mask, in order of their first cell in row-major scan.
    public static unsafe int[] Label(bool[,] mask, int width, int height, out List<int[]> components)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var bytes = CoreNative.Bytes(mask);
        var labels = new int[Math.Max(width * height, 1)];
        int* cells = null;
        int* offsets = null;
        int count;
        fixed (byte* m = bytes)
        fixed (int* l = labels)
        {
            CoreNative.Check(CoreNative.mn_label(m, width, height, l, &cells, &offsets, &count));
        }

        try
        {
            components = new List<int[]>(count);
            for (var c = 0; c < count; c++)
            {
                components.Add(new ReadOnlySpan<int>(cells + offsets[c], offsets[c + 1] - offsets[c]).ToArray());
            }
        }
        finally
        {
            CoreNative.mn_free(cells);
            CoreNative.mn_free(offsets);
        }

        return width * height == 0 ? Array.Empty<int>() : labels;
    }
}
