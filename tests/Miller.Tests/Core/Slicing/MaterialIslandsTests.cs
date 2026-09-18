using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Slicing;

// Islands are the enclosed components of standing stock above the reach floor; the frame touches
// the outside; an island below the volume to keep goes back to the masks, the coverage and the
// standing map.
public sealed class MaterialIslandsTests
{
    private const float Cell = 1f;
    private const float Top = 5f;
    private const int Size = 20;

    // Standing: frame at Top outside a trench ring at the floor (x or y in 3 or 16), an island
    // inside: core at Top for 6 .. 13, one-cell terrace at 2 around it (5 and 14).
    private static (HeightMap Standing, HeightMap Tip, HeightMap Stock) Synthetic()
    {
        var standing = new HeightMap(0, 0, Cell, Size, Size, Top);
        var tip = new HeightMap(0, 0, Cell, Size, Size, 0f);
        var stock = new HeightMap(0, 0, Cell, Size, Size, Top);
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                var ring = Math.Max(Math.Abs(i - 9.5f), Math.Abs(j - 9.5f));
                if (ring >= 6 && ring < 7)
                {
                    standing[i, j] = 0f;
                }
                else if (ring >= 4 && ring < 6)
                {
                    standing[i, j] = 2f;
                }
            }
        }

        return (standing, tip, stock);
    }

    [Fact]
    public void Find_ReturnsTheEnclosedComponentWithItsVolume_AndNotTheFrame()
    {
        var (standing, tip, stock) = Synthetic();
        var island = Assert.Single(MaterialIslands.Find(standing, tip, stock, 0.05f));
        // Core 8 x 8 at 5 plus a 12 x 12 - 8 x 8 terrace at 2.
        Assert.Equal(144, island.Cells.Length);
        Assert.Equal(64 * 5f + 80 * 2f, island.Volume, 3);
        Assert.All(island.Cells, c => Assert.InRange(c % Size, 4, 15));
    }

    [Fact]
    public void Find_TreatsAComponentNextToACellWithoutStockAsTheFrame()
    {
        var (standing, tip, stock) = Synthetic();
        stock[9, 9] = float.NaN;
        Assert.Empty(MaterialIslands.Find(standing, tip, stock, 0.05f));
        var (again, tip2, stock2) = Synthetic();
        // A gap in the trench ring joins the island to the frame.
        again[3, 9] = Top;
        Assert.Empty(MaterialIslands.Find(again, tip2, stock2, 0.05f));
        Assert.Throws<ArgumentException>(() => MaterialIslands.Find(again, tip2, new HeightMap(0, 0, Cell, Size + 1, Size, Top), 0.05f));
    }

    [Fact]
    public void RemoveBelow_HandsTheIslandBackToMasksCoverageAndStanding_StrictlyBelowTheThreshold()
    {
        var (standing, tip, stock) = Synthetic();
        var islands = MaterialIslands.Find(standing, tip, stock, 0.05f);
        var allowed = new List<bool[,]> { Filled(true), Filled(true) };
        var masks = new[] { Filled(false), Filled(false) };
        var coverage = Filled(false);
        var full = Filled(true);

        Assert.Empty(MaterialIslands.RemoveBelow(islands, islands[0].Volume, allowed, masks, full, coverage, standing));
        Assert.False(masks[0][9, 9]);
        Assert.Equal(Top, standing[9, 9]);

        var removed = Assert.Single(MaterialIslands.RemoveBelow(islands, islands[0].Volume + 1f, allowed, masks, full, coverage, standing));
        Assert.Same(islands[0], removed);
        Assert.True(masks[0][9, 9]);
        Assert.True(masks[1][5, 9]);
        Assert.True(coverage[9, 9]);
        Assert.True(float.IsNaN(standing[9, 9]));
        Assert.True(float.IsNaN(standing[5, 9]));
        Assert.False(masks[0][0, 0]);
        Assert.Equal(Top, standing[0, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterialIslands.RemoveBelow(islands, -1f, allowed, masks, full, coverage, standing));
    }

    // Square ring of four 10 mm wide bars (outer 40 x 40, hole 20 x 20, 5 high) in a 50 x 50 x 5
    // stock; the 6 mm cutter's axis stops one radius before the bars, which leaves the bars whole.
    public static Mesh Ring()
    {
        var bars = new[]
        {
            TestMeshes.Box(40, 10, 5),
            TestMeshes.Box(40, 10, 5).Transform(Matrix4x4.CreateTranslation(0, 30, 0)),
            TestMeshes.Box(10, 20, 5).Transform(Matrix4x4.CreateTranslation(0, 10, 0)),
            TestMeshes.Box(10, 20, 5).Transform(Matrix4x4.CreateTranslation(30, 10, 0)),
        };
        return new Mesh(bars.SelectMany(b => b.Triangles));
    }

    public static StockDefinition RingStock() => new() { Shape = StockShape.Box, SizeX = 50, SizeY = 50, SizeZ = 5 };

    [Fact]
    public void Separation_MillsOutTheHoleIslandBelowTheVolume_AndKeepsItAbove()
    {
        var context = TestContexts.Build(Ring(), RingStock(), TestContexts.FlatTool6(), TestContexts.Parameters());
        var kept = SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, context.Parameters, context.StockTop, 0f, 0f);
        var island = Assert.Single(MaterialIslands.Find(kept.Standing, context.EffectiveTip, context.Stock, context.Parameters.Tolerance));
        Assert.Empty(kept.MilledIslands);
        // The tool axis stays one radius (3 mm) inside the 20 x 20 hole walls, so the floor-level
        // positions form a 14 x 14 square and the island is that square minus the one-cell trench
        // band along its outline: 12 to 14 mm square, 5 deep.
        Assert.InRange(island.Volume, 12 * 12 * 5f, 14 * 14 * 5f);
        var (ci, cj) = context.Stock.CellOf(25f, 25f);
        Assert.Equal(context.StockTop, kept.Standing[ci, cj], 3);
        Assert.False(kept.Plan.Steps[^1].Mask[ci, cj]);

        var milled = SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, context.Parameters, context.StockTop, 0f, island.Volume + 1f);
        var removed = Assert.Single(milled.MilledIslands);
        Assert.Equal(island.Volume, removed.Volume, 3);
        Assert.True(float.IsNaN(milled.Standing[ci, cj]));
        Assert.All(milled.Plan.Steps, s => Assert.True(s.Mask[ci, cj]));
        Assert.True(milled.Plan.Coverage[ci, cj]);
        // The frame outside the ring stands whatever the threshold.
        Assert.Equal(context.StockTop, milled.Standing[0, 0], 3);
        Assert.Equal(context.StockTop, SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, context.Parameters, context.StockTop, 0f, 1e9f).Standing[0, 0], 3);
    }

    private static bool[,] Filled(bool value)
    {
        var mask = new bool[Size, Size];
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                mask[i, j] = value;
            }
        }

        return mask;
    }
}
