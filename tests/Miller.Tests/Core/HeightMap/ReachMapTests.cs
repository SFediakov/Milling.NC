using System.Diagnostics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

// The reach floor is voted in rounds over the footprint cells that hold stock (ReachMapRoundsTests
// covers the rounds); here the fixtures, the helpers, the bounds and the timing.
public sealed class ReachMapTests
{
    private const float Cell = 0.5f;
    private const float Wall = 5f;

    // A 1 mm flat tool on 0.5 mm cells touches five cells: the center and its four edge neighbours.
    private static ToolProfile PlusProfile(TipType tip = TipType.Flat)
        => ToolProfile.Create(new ToolDefinition { TipType = tip, CutterDiameter = 1f, HeadDiameter = 2f, CutterLength = 10f }, Cell);

    // 20 x 20 cells, model at Wall for i >= 10 and at the floor 0 elsewhere, stock flat at Wall.
    private static (HeightMap Model, HeightMap Stock) WallAtTen()
    {
        var model = new HeightMap(0, 0, Cell, 20, 20, 0f);
        var stock = new HeightMap(0, 0, Cell, 20, 20, Wall);
        for (var j = 0; j < 20; j++)
        {
            for (var i = 10; i < 20; i++)
            {
                model[i, j] = Wall;
            }
        }

        return (model, stock);
    }

    [Fact]
    public void MixedFootprints_KeepTheWallTop_WhenTheFloorIsReachedWithoutThem()
    {
        var (model, stock) = WallAtTen();
        Assert.Equal(5, PlusProfile().Offsets.Length);
        var reach = ReachMap.Compute(model, stock, PlusProfile(), 0f);
        // One wall cell of five: the four floor cells are reached by the positions further left in
        // the drop-cutter round, so nothing is left to gain and the wall top stays. Four of five: not.
        Assert.Equal(Wall, reach[9, 5]);
        Assert.Equal(Wall, reach[10, 5]);
        Assert.Equal(0f, reach[8, 5]);
        Assert.Equal(Wall, reach[11, 5]);

        // Two of four (the cell below holds no stock): the same, the floor cells are already reached.
        stock[9, 4] = float.NaN;
        model[9, 6] = Wall;
        var tied = ReachMap.Compute(model, stock, PlusProfile(), 0f);
        Assert.Equal(Wall, tied[9, 5]);
        // Three of four once the left cell is wall too.
        model[8, 5] = Wall;
        Assert.Equal(Wall, ReachMap.Compute(model, stock, PlusProfile(), 0f)[9, 5]);
    }

    [Fact]
    public void ReachableAt_UsesTheSlicerSlack()
    {
        Assert.True(ReachMap.ReachableAt(3f, 3f));
        Assert.True(ReachMap.ReachableAt(3f + Slicer.LevelTolerance / 2, 3f));
        Assert.False(ReachMap.ReachableAt(3f + 2 * Slicer.LevelTolerance, 3f));
        Assert.False(ReachMap.ReachableAt(float.NaN, 3f));
    }

    [Fact]
    public void StraightWall_IsPreserved_TheAxisStopsOneRadiusBeforeIt()
    {
        var (model, stock) = WallAtTen();
        var tool = new ToolDefinition { CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };
        var profile = ToolProfile.Create(tool, Cell);
        var reach = ReachMap.Compute(model, stock, profile, 0f);
        var drop = HeightMapDilation.ComputeTipMap(model, profile);
        for (var j = 3; j < 17; j++)
        {
            // Axis within one radius of the wall line: wall top, like the drop cutter, because the
            // floor beside the wall is reached from further out; on or inside it: wall top.
            Assert.Equal(Wall, reach[9, j]);
            Assert.Equal(Wall, reach[10, j]);
            // The drop cutter stops one radius (6 cells) before the wall.
            Assert.Equal(Wall, drop[4, j]);
            Assert.Equal(0f, drop[3, j]);
            Assert.Equal(0f, reach[3, j]);
        }

        for (var k = 0; k < reach.CellCount; k++)
        {
            Assert.True(reach.Z[k] <= drop.Z[k] + 1e-6f, "the reach floor is never above the drop cutter");
        }
    }

    [Fact]
    public void UniformFootprint_EqualsTheDropCutter()
    {
        var context = TestContexts.BoxInStock();
        var drop = HeightMapDilation.ComputeTipMap(context.Model, context.Profile);
        var (fi, fj) = context.Model.CellOf(1f, 1f);
        var (ti, tj) = context.Model.CellOf(10f, 10f);
        Assert.Equal(drop[fi, fj], context.Tip[fi, fj], 4);
        Assert.Equal(drop[ti, tj], context.Tip[ti, tj], 4);
        Assert.Equal(0f, context.Tip[fi, fj]);
        Assert.Equal(5f, context.Tip[ti, tj], 3);
    }

    [Fact]
    public void LevelMasks_AreNested_BecauseTheFloorIsMonotone()
    {
        var context = TestContexts.BumpPlate();
        var steps = context.Plan.Steps;
        Assert.True(steps.Count > 2);
        for (var k = 1; k < steps.Count; k++)
        {
            for (var j = 0; j < steps[k].Height; j++)
            {
                for (var i = 0; i < steps[k].Width; i++)
                {
                    Assert.True(!steps[k].Mask[i, j] || steps[k - 1].Mask[i, j], $"cell {i},{j} reachable at {steps[k].Level} but not at {steps[k - 1].Level}");
                }
            }
        }
    }

    // A ball bottom stands r - sqrt(r^2 - d^2) above the tip at distance d, so each footprint cell
    // votes with model - dz. Over a flat top the four edge cells (dz = r here) would outvote the
    // center, but every top cell is reached through its own position in the drop-cutter round, so
    // the later rounds have nothing to gain there and the top keeps its height: no dimple. The
    // column beside the wall is not reached by the drop cutter (its own position is held up by the
    // wall edge, the neighbours' edges stay above it) and is entered in the second round.
    [Fact]
    public void BallTip_CountsTheToolBottomHeightPerCell()
    {
        var (model, stock) = WallAtTen();
        var flat = ReachMap.Compute(model, stock, PlusProfile(TipType.Flat), 0f);
        var ball = ReachMap.Compute(model, stock, PlusProfile(TipType.Ball), 0f);
        var edge = ToolProfile.BottomHeight(TipType.Ball, 0.5f, Cell);
        Assert.Equal(0.5f, edge, 4);
        Assert.Equal(Wall, flat[10, 5]);
        Assert.Equal(Wall, flat[12, 5]);
        Assert.Equal(Wall, ball[10, 5], 4);
        Assert.Equal(Wall, ball[12, 5], 4);
        Assert.Equal(0f, ball[9, 5]);
        Assert.Equal(Wall, flat[9, 5]);
    }

    [Fact]
    public void NoStockUnderTheFootprint_IsNaN_AndNaNModelCountsAsFloor()
    {
        var model = new HeightMap(0, 0, Cell, 10, 10, float.NaN);
        var stock = new HeightMap(0, 0, Cell, 10, 10, float.NaN);
        stock[5, 5] = 4f;
        var reach = ReachMap.Compute(model, stock, PlusProfile(), -1f);
        Assert.Equal(-1f, reach[5, 5]);
        Assert.Equal(-1f, reach[6, 5]);
        // The ball edge would vote 0.5 under the floor; the floor is the lower bound.
        var ball = ReachMap.Compute(new HeightMap(0, 0, Cell, 10, 10, 0f), new HeightMap(0, 0, Cell, 10, 10, 4f), PlusProfile(TipType.Ball), 0f);
        Assert.Equal(0f, ball[5, 5]);
        Assert.True(float.IsNaN(reach[8, 8]));
        Assert.Throws<ArgumentException>(() => ReachMap.Compute(model, new HeightMap(0, 0, Cell, 9, 10, 1f), PlusProfile(), 0f));
    }

    [Fact]
    public void Select_MatchesSorting_ForEveryRank()
    {
        var values = new float[] { 5, 3, 9, 1, 3, 7, 0, 2, 8, 6, 4, 3 };
        var sorted = values.OrderBy(v => v).ToArray();
        for (var rank = 0; rank < values.Length; rank++)
        {
            var copy = (float[])values.Clone();
            Assert.Equal(sorted[rank], ReachMap.Select(copy, rank));
        }

        Assert.Equal(4f, ReachMap.Select(new float[] { 4 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.Select(new float[] { 1, 2 }, 2));
    }

    [Fact]
    public void HeartSizedGrid_ComputesInUnderTwoSeconds()
    {
        var model = new HeightMap(0, 0, 0.1f, 300, 300, 0f);
        var stock = new HeightMap(0, 0, 0.1f, 300, 300, 5.1f);
        for (var k = 0; k < model.CellCount; k++)
        {
            model.Z[k] = (k * 7919) % 51 * 0.1f;
        }

        var profile = ToolProfile.Create(new ToolDefinition { CutterDiameter = 1.2f, HeadDiameter = 10f, CutterLength = 50f }, 0.1f);
        Assert.Equal(113, profile.Offsets.Length);
        var watch = Stopwatch.StartNew();
        var reach = ReachMap.Compute(model, stock, profile, 0f);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"reach map took {watch.Elapsed}");
        Assert.Equal(model.CellCount, reach.MaterialCellCount());
    }
}
