using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Xunit;

namespace Miller.Tests.Core.Simulation;

public sealed class MaterialRemoverTests
{
    private const float Cell = 0.5f;
    private const float StockTop = 5f;

    // 10 x 10 mm stock, 20 x 20 cells, centers at 0.25, 0.75, ...
    private static HeightMap Stock() => new(0, 0, Cell, 20, 20, StockTop);

    private static ToolDefinition Tool(TipType tip, float diameter) => new() { CutterDiameter = diameter, CutterLength = 20, HeadDiameter = diameter + 4, TipType = tip };

    // Distance in cells from cell (i, j) to the run of cells [i0, i1] on row j0.
    private static float CellDistance(int i, int j, int i0, int i1, int j0)
    {
        var di = i < i0 ? i0 - i : i > i1 ? i - i1 : 0;
        return MathF.Sqrt(di * di + (j - j0) * (j - j0));
    }

    [Fact]
    public void FlatTool_LeavesAChannelOfTheToolWidthAtTheTipHeight()
    {
        var stock = Stock();
        var tool = Tool(TipType.Flat, 2.2f);
        var profile = ToolProfile.Create(tool, Cell);
        var from = new Vector3(2.25f, 5.25f, 3f);
        var to = new Vector3(7.25f, 5.25f, 3f);
        var dirty = MaterialRemover.Sweep(stock, profile, from, to);

        var (i0, j0) = stock.CellOf(from.X, from.Y);
        var (i1, _) = stock.CellOf(to.X, to.Y);
        var expectedDirty = DirtyRect.Empty;
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width; i++)
            {
                var inside = Cell * CellDistance(i, j, i0, i1, j0) <= tool.CutterRadius + ToolProfile.RadiusTolerance;
                Assert.Equal(inside ? 3f : StockTop, stock[i, j]);
                if (inside)
                {
                    expectedDirty = expectedDirty.Include(i, j);
                }
            }
        }

        // 2 cells each side of the axis row: 5 rows of 0.5 mm hold every center within 1.1 mm.
        Assert.Equal(j0 - 2, expectedDirty.J0);
        Assert.Equal(j0 + 2, expectedDirty.J1);
        Assert.Equal(expectedDirty, dirty);
    }

    [Fact]
    public void BallTool_LeavesARoundedChannel()
    {
        var stock = Stock();
        var tool = Tool(TipType.Ball, 3f);
        var profile = ToolProfile.Create(tool, Cell);
        var y = 5.25f;
        MaterialRemover.Sweep(stock, profile, new Vector3(2.25f, y, 3f), new Vector3(7.25f, y, 3f));

        var (_, j0) = stock.CellOf(4.25f, y);
        var (i, _) = stock.CellOf(4.25f, y);
        Assert.Equal(3f, stock[i, j0]);
        for (var dj = 1; dj <= 3; dj++)
        {
            var d = Cell * dj;
            var expected = 3f + ToolProfile.BottomHeight(TipType.Ball, tool.CutterRadius, d);
            Assert.Equal(expected, stock[i, j0 + dj], 5);
            Assert.Equal(expected, stock[i, j0 - dj], 5);
            Assert.True(stock[i, j0 + dj] > stock[i, j0 + dj - 1], "the channel wall must rise away from the axis");
        }

        Assert.Equal(StockTop, stock[i, j0 + 4]);
    }

    [Fact]
    public void RapidAtSafeHeight_ChangesNothing()
    {
        var stock = Stock();
        var profile = ToolProfile.Create(Tool(TipType.Flat, 6f), Cell);
        var dirty = MaterialRemover.Sweep(stock, profile, new Vector3(0, 0, StockTop + 5), new Vector3(10, 10, StockTop + 5));
        Assert.True(dirty.IsEmpty);
        Assert.Equal(0, dirty.Width);
        Assert.All(stock.Z, z => Assert.Equal(StockTop, z));
    }

    [Fact]
    public void Plunge_CutsOnlyTheFootprint_AndDirtyRectIsExact()
    {
        var stock = Stock();
        var tool = Tool(TipType.Flat, 2f);
        var profile = ToolProfile.Create(tool, Cell);
        var tip = new Vector3(5.25f, 5.25f, 1f);
        var dirty = MaterialRemover.Sweep(stock, profile, tip with { Z = StockTop + 2 }, tip);
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        Assert.Equal(new DirtyRect(ci - 2, cj - 2, ci + 2, cj + 2), dirty);
        Assert.Equal(5, dirty.Width);
        Assert.Equal(5, dirty.Height);
        Assert.Equal(1f, stock[ci, cj]);
        Assert.Equal(1f, stock[ci + 2, cj]);
        Assert.Equal(StockTop, stock[ci + 2, cj + 2]);
        Assert.Equal(StockTop, stock[ci + 3, cj]);

        // A second, shallower pass over the same spot changes nothing and reports no dirty cells.
        Assert.True(MaterialRemover.Sweep(stock, profile, tip with { Z = 2f }, tip with { Z = 2f }).IsEmpty);
    }

    [Fact]
    public void EmptyCellsStayEmpty_AndEdgesAreClipped()
    {
        var stock = Stock();
        stock[0, 0] = float.NaN;
        stock[1, 0] = float.NaN;
        var profile = ToolProfile.Create(Tool(TipType.Flat, 2f), Cell);
        var dirty = MaterialRemover.Sweep(stock, profile, new Vector3(-1f, 0.25f, 2f), new Vector3(1.25f, 0.25f, 2f));
        Assert.True(float.IsNaN(stock[0, 0]));
        Assert.True(float.IsNaN(stock[1, 0]));
        Assert.Equal(2f, stock[2, 0]);
        Assert.Equal(2f, stock[0, 1]);
        Assert.Equal(0, dirty.I0);
        Assert.Equal(0, dirty.J0);
        Assert.Equal(2, dirty.J1);
    }

    [Fact]
    public void ProfileMustMatchTheStockGrid()
    {
        var stock = Stock();
        var profile = ToolProfile.Create(Tool(TipType.Flat, 2f), 0.25f);
        Assert.Throws<ArgumentException>(() => MaterialRemover.Sweep(stock, profile, Vector3.Zero, Vector3.One));
    }

    [Fact]
    public void DirtyRect_UnionAndEmptyRules()
    {
        var empty = DirtyRect.Empty;
        Assert.True(empty.IsEmpty);
        var a = new DirtyRect(2, 3, 4, 5);
        Assert.Equal(a, empty.Union(a));
        Assert.Equal(a, a.Union(empty));
        Assert.Equal(new DirtyRect(1, 3, 4, 9), a.Union(new DirtyRect(1, 8, 2, 9)));
        Assert.Equal(new DirtyRect(2, 3, 7, 5), a.Include(7, 4));
    }
}
