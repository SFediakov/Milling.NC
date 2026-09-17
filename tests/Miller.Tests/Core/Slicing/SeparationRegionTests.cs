using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Slicing;

public sealed class SeparationRegionTests
{
    private static int Count(bool[,] mask) => mask.Cast<bool>().Count(b => b);

    [Fact]
    public void HeadReach_FindsTheShallowestLevelWhoseHeadEntersTheSlab()
    {
        // Levels 28 .. 0 in a 30 mm stock with a 12 mm cutter: the first slab (top 30) is entered by
        // the head of every level below 18, the shallowest being 16 (index 6); slabs with a top of
        // 12 or less are entered by none.
        var steps = Enumerable.Range(1, 15).Select(k => new MillingStep(30f - 2f * k, new bool[1, 1])).ToList();
        Assert.Equal(6, SeparationRegion.HeadReach(steps, 0, 30f, 12f));
        Assert.Equal(7, SeparationRegion.HeadReach(steps, 1, 30f, 12f));
        Assert.Equal(-1, SeparationRegion.HeadReach(steps, 9, 30f, 12f));
        Assert.Equal(-1, SeparationRegion.HeadReach(steps, 14, 30f, 12f));
        Assert.Equal(-1, SeparationRegion.HeadReach(steps, 0, 30f, 40f));
    }

    [Fact]
    public void Within_MarksCellsUpToTheRadius()
    {
        var marked = new bool[9, 9];
        marked[4, 4] = true;
        var within = SeparationRegion.Within(marked, 1f, 1.5f);
        Assert.True(within[4, 4]);
        Assert.True(within[5, 5]);
        Assert.True(within[4, 3]);
        Assert.False(within[6, 4]);
        Assert.False(within[6, 6]);
        Assert.Equal(9, within.Cast<bool>().Count(b => b));
    }

    [Fact]
    public void BoxInStock_KeepsOnlyTheModelRegionAndABandAroundIt()
    {
        var context = TestContexts.BoxInStock();
        var floor = 0f;
        var scoped = SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, context.Parameters, context.StockTop, floor, 0f);
        var everything = SeparationRegion.Everything(context.Plan, context.EffectiveTip);
        Assert.Same(context.Plan, everything.Plan);
        Assert.All(everything.Standing.Z, z => Assert.True(float.IsNaN(z)));

        var region = SeparationRegion.ModelRegion(context.EffectiveTip, floor);
        var full = context.Plan.Steps.ToList();
        var restricted = scoped.Plan.Steps.ToList();
        Assert.Equal(full.Count, restricted.Count);
        var cellSize = context.EffectiveTip.CellSize;
        var adjacency = SeparationRegion.AdjacencyMargin(cellSize);
        for (var k = 0; k < full.Count; k++)
        {
            Assert.Equal(full[k].Level, restricted[k].Level);
            Assert.True(restricted[k].MaskCount < full[k].MaskCount, $"level {full[k].Level}: nothing left out");
            // Every restricted cell is allowed, and lies in the model region or next to an obstacle.
            for (var j = 0; j < context.EffectiveTip.Height; j++)
            {
                for (var i = 0; i < context.EffectiveTip.Width; i++)
                {
                    if (!restricted[k].Mask[i, j])
                    {
                        continue;
                    }

                    Assert.True(full[k].Mask[i, j]);
                    if (region[i, j])
                    {
                        continue;
                    }

                    var near = false;
                    for (var dj = -1; dj <= 1 && !near; dj++)
                    {
                        for (var di = -1; di <= 1; di++)
                        {
                            var ii = i + di;
                            var jj = j + dj;
                            if (context.EffectiveTip.InBounds(ii, jj) && context.EffectiveTip[ii, jj] > full[k].Level)
                            {
                                near = true;
                                break;
                            }
                        }
                    }

                    Assert.True(near, $"cell {i},{j} at level {full[k].Level} is neither model nor adjacent (band {adjacency})");
                }
            }
        }

        // The stock corner cell is never cut: it stands at the stock top, the model region is NaN.
        Assert.Equal(context.StockTop, scoped.Standing[0, 0], 3);
        var (ci, cj) = context.EffectiveTip.CellOf(10f, 10f);
        Assert.True(float.IsNaN(scoped.Standing[ci, cj]));
        // Cells of the innermost band stand at the deepest level.
        var deepest = restricted[^1];
        var bandCell = Enumerable.Range(0, context.EffectiveTip.Width * context.EffectiveTip.Height)
            .Select(k => (I: k % context.EffectiveTip.Width, J: k / context.EffectiveTip.Width))
            .First(c => deepest.Mask[c.I, c.J] && !region[c.I, c.J]);
        Assert.Equal(deepest.Level, scoped.Standing[bandCell.I, bandCell.J]);
        // The coverage keeps the model region and the innermost band only.
        var coverage = scoped.Plan.Coverage;
        Assert.True(Count(coverage) < Count(context.Plan.Coverage));
        Assert.True(coverage[ci, cj]);
        Assert.False(coverage[0, 0]);
        Assert.True(coverage[bandCell.I, bandCell.J]);
    }

    [Fact]
    public void Masks_AreMonotone_DeeperCellsAreCutAtEveryLevelAbove()
    {
        var context = TestContexts.BumpPlate();
        var scoped = SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, context.Parameters, context.StockTop, 0f, 0f);
        var steps = scoped.Plan.Steps.ToList();
        var full = context.Plan.Steps.ToList();
        for (var k = 1; k < steps.Count; k++)
        {
            for (var j = 0; j < context.EffectiveTip.Height; j++)
            {
                for (var i = 0; i < context.EffectiveTip.Width; i++)
                {
                    if (steps[k].Mask[i, j] && full[k - 1].Mask[i, j])
                    {
                        Assert.True(steps[k - 1].Mask[i, j], $"cell {i},{j} cut at {steps[k].Level} but not at {steps[k - 1].Level}");
                    }
                }
            }
        }
    }

    [Fact]
    public void DeepStock_TerracesTheTrench()
    {
        // 10 x 10 x 5 box on top of a 40 x 40 x 30 stock with a 12 mm cutter: the trench must widen
        // twice on the way up so the head clears the stock walls.
        var tool = new ToolDefinition { CutterDiameter = 6, HeadDiameter = 10, CutterLength = 12 };
        var parameters = TestContexts.Parameters(0.5f);
        var context = TestContexts.Build(TestMeshes.Box(10, 10, 5), new StockDefinition { SizeX = 40, SizeY = 40, SizeZ = 30 }, tool, parameters);
        var scoped = SeparationRegion.Build(context.Plan, context.EffectiveTip, context.Stock, context.Tool, parameters, context.StockTop, context.Plan.LowestLevel, 0f);
        var steps = scoped.Plan.Steps.ToList();
        var top = steps[0];
        var bottom = steps[^1];
        Assert.True(top.MaskCount > bottom.MaskCount * 2, $"top {top.MaskCount} cells, bottom {bottom.MaskCount}: no terrace");

        // Trench width on the row through the model center, measured on the +X side of the model.
        var map = context.EffectiveTip;
        var region = SeparationRegion.ModelRegion(map, context.Plan.LowestLevel);
        var (_, jc) = map.CellOf(20f, 20f);
        int Width(MillingStep step)
        {
            var count = 0;
            for (var i = map.Width / 2; i < map.Width; i++)
            {
                if (step.Mask[i, jc] && !region[i, jc])
                {
                    count++;
                }
            }

            return count;
        }

        // Two terraces, each one head overhang wider than the region below it.
        var overhangCells = (tool.HeadRadius - tool.CutterRadius + SeparationRegion.HeadMargin(map.CellSize, parameters.Tolerance)) / map.CellSize;
        Assert.InRange(Width(bottom), 1, 3);
        Assert.InRange(Width(top) - Width(bottom), 2 * overhangCells - 2, 2 * overhangCells + 2);
        Assert.Equal(context.StockTop, scoped.Standing[0, 0]);
    }
    [Fact]
    public void CylinderStock_KeepsNaNOutsideTheCircle_AndFlatModelNeedsNoLevel()
    {
        var parameters = TestContexts.Parameters();
        var cylinder = TestContexts.Build(TestMeshes.Box(10, 10, 5), new StockDefinition { Shape = StockShape.Cylinder, Diameter = 30, Height = 5 }, TestContexts.FlatTool6(), parameters);
        var scoped = SeparationRegion.Build(cylinder.Plan, cylinder.EffectiveTip, cylinder.Stock, cylinder.Tool, parameters, cylinder.StockTop, 0f, 0f);
        Assert.True(float.IsNaN(scoped.Standing[0, 0]));
        Assert.True(float.IsNaN(cylinder.Stock[0, 0]));
        var (ci, cj) = cylinder.Stock.CellOf(15f, 1f);
        Assert.False(float.IsNaN(cylinder.Stock[ci, cj]));
        Assert.Equal(cylinder.StockTop, scoped.Standing[ci, cj], 3);
        Assert.True(scoped.Plan.Steps.All(s => s.MaskCount > 0));

        // A plate filling the stock top: no level, the coverage keeps every material cell.
        var flat = TestContexts.Build(TestMeshes.Box(20, 20, 5), new StockDefinition { SizeX = 20, SizeY = 20, SizeZ = 5 }, TestContexts.FlatTool6(), parameters);
        Assert.Equal(0, flat.Plan.Levels);
        var flatScoped = SeparationRegion.Build(flat.Plan, flat.EffectiveTip, flat.Stock, flat.Tool, parameters, flat.StockTop, 0f, 0f);
        Assert.Equal(flat.Plan.Coverage.Cast<bool>().Count(b => b), flatScoped.Plan.Coverage.Cast<bool>().Count(b => b));
        Assert.All(flatScoped.Standing.Z, z => Assert.True(float.IsNaN(z)));
    }
}
