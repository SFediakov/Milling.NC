using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// Components per level become a tree: a component of a deeper level hangs under the component of
// the level above that contains it; nodes on the lattice and the outline are picked per region.
public sealed class CaveTreeTests
{
    private static bool[,] Mask(int width, int height, Func<int, int, bool> inside)
    {
        var mask = new bool[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                mask[i, j] = inside(i, j);
            }
        }

        return mask;
    }

    [Fact]
    public void Label_FindsEightConnectedComponents_InScanOrder()
    {
        var mask = Mask(6, 6, (i, j) => (i < 2 && j < 2) || (i == 2 && j == 2) || (i >= 4 && j >= 4));
        var labels = CaveTree.Label(mask, 6, 6, out var components);
        // The 2 x 2 block touches (2, 2) diagonally: one component; the far block is another.
        Assert.Equal(2, components.Count);
        Assert.Equal(5, components[0].Length);
        Assert.Equal(4, components[1].Length);
        Assert.Equal(0, labels[0]);
        Assert.Equal(0, labels[2 * 6 + 2]);
        Assert.Equal(1, labels[5 * 6 + 5]);
        Assert.Equal(-1, labels[3]);
    }

    [Fact]
    public void Build_NestsTheDeeperComponentsUnderTheirParents()
    {
        // Level 0: one region over the whole grid. Level 1: two pockets. Level 2: the left pocket only.
        var steps = new List<MillingStep>
        {
            new(4f, Mask(10, 4, (i, j) => true)),
            new(2f, Mask(10, 4, (i, j) => i < 4 || i > 5)),
            new(0f, Mask(10, 4, (i, j) => i < 4)),
        };
        var tree = CaveTree.Build(steps);
        var root = Assert.Single(tree.Roots);
        Assert.Equal(0, root.Level);
        Assert.Equal(40, root.Cells.Length);
        Assert.Equal(2, root.Children.Count);
        var left = root.Children[0];
        var right = root.Children[1];
        Assert.Equal(16, left.Cells.Length);
        Assert.Equal(16, right.Cells.Length);
        var deepest = Assert.Single(left.Children);
        Assert.Equal(2, deepest.Level);
        Assert.Empty(right.Children);
        Assert.Equal(3, tree.Labels.Length);
        Assert.Equal(-1, tree.Labels[1][4]);
    }

    [Fact]
    public void Build_StartsARootForAComponentWithoutAParent_AndAcceptsNoSteps()
    {
        var steps = new List<MillingStep>
        {
            new(4f, Mask(4, 4, (i, j) => i < 2)),
            new(2f, Mask(4, 4, (i, j) => i >= 2)),
        };
        var tree = CaveTree.Build(steps);
        Assert.Equal(2, tree.Roots.Count);
        Assert.Equal(1, tree.Roots[1].Level);
        Assert.Empty(CaveTree.Build(Array.Empty<MillingStep>()).Roots);
        var mismatched = new List<MillingStep> { new(4f, new bool[4, 4]), new(2f, new bool[5, 4]) };
        Assert.Throws<ArgumentException>(() => CaveTree.Build(mismatched));
    }

    [Fact]
    public void NodeLattice_TakesLatticeCrossingsAndTheOutline()
    {
        // 12 x 12 grid, region i, j in 2..9, lattice step 3.
        var nodes = NodeLattice.Nodes((i, j) => i >= 2 && i <= 9 && j >= 2 && j <= 9, 12, 12, 3);
        var set = nodes.ToHashSet();
        // Outline: the ring of the 8 x 8 square, 28 cells; interior lattice crossings at 3, 6 (both).
        Assert.Contains(3 * 12 + 3, set);
        Assert.Contains(6 * 12 + 6, set);
        Assert.Contains(3 * 12 + 6, set);
        Assert.DoesNotContain(4 * 12 + 4, set);
        Assert.DoesNotContain(5 * 12 + 3, set);
        Assert.Equal(28 + 4, nodes.Count);
        Assert.Equal(nodes.OrderBy(n => n), nodes);
        Assert.Equal(6, NodeLattice.StepCells(3f, 0.5f));
        Assert.Equal(1, NodeLattice.StepCells(0.05f, 0.1f));
        Assert.True(NodeLattice.OnLattice(0, 12, 3));
        Assert.True(NodeLattice.OnLattice(11, 12, 3));
        Assert.False(NodeLattice.OnLattice(10, 12, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeLattice.Nodes((i, j) => true, 2, 2, 0));
    }

    [Fact]
    public void NodeLattice_GridEdgeIsNotAnOutline_ButTheLastLineIsALatticeLine()
    {
        var nodes = NodeLattice.Nodes((i, j) => true, 7, 5, 3);
        // Lattice lines i in {0, 3, 6}, j in {0, 3, 4}: nine crossings, no outline cells.
        Assert.Equal(9, nodes.Count);
        Assert.Contains(4 * 7 + 6, nodes);
        Assert.Contains(3 * 7 + 3, nodes);
    }
}
