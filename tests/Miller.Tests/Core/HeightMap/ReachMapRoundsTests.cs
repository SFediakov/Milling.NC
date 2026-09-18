using Miller.Core.HeightMaps;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

// The reach map in rounds: the drop cutter first, then halfway to the user's percent, then the
// percent itself; cells an accepted position brings to the model top stop counting as intended,
// unintended cells always count, and floors only go down between rounds.
public sealed class ReachMapRoundsTests
{
    private const float Cell = 0.5f;
    private const float Wall = 5f;

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

    // The user's example: 20 footprint cells, 15 intended and 5 unintended at the level, 10 of the
    // intended ones already reached. Plain vote 75 percent; discounted 5 of 10, 50 percent.
    [Fact]
    public void SelectThreshold_DiscountsReachedIntendedCells()
    {
        var values = new float[20];
        var flags = new bool[20];
        for (var k = 15; k < 20; k++)
        {
            values[k] = Wall;
        }

        for (var k = 0; k < 10; k++)
        {
            flags[k] = true;
        }

        Assert.Equal(0f, ReachMap.SelectThreshold((float[])values.Clone(), new bool[20], 60));
        Assert.Equal(Wall, ReachMap.SelectThreshold((float[])values.Clone(), (bool[])flags.Clone(), 60));
        Assert.Equal(0f, ReachMap.SelectThreshold((float[])values.Clone(), (bool[])flags.Clone(), 50));
    }

    // Cells the model stands in keep voting against a level after an earlier round cut them.
    [Fact]
    public void SelectThreshold_CountsReachedUnintendedCells()
    {
        var values = new float[] { 0, 0, Wall, Wall, Wall };
        var flags = new[] { false, false, true, true, true };
        Assert.Equal(Wall, ReachMap.SelectThreshold((float[])values.Clone(), (bool[])flags.Clone(), 50));
        Assert.Equal(0f, ReachMap.SelectThreshold((float[])values.Clone(), (bool[])flags.Clone(), 40));
    }

    [Fact]
    public void SelectThreshold_At100_IsTheMaximum_WhateverIsExcluded()
    {
        var values = new float[] { 3, 1, 4, 1, 5, 9, 2, 6 };
        Assert.Equal(9f, ReachMap.SelectThreshold((float[])values.Clone(), new bool[8], 100));
        Assert.Equal(9f, ReachMap.SelectThreshold((float[])values.Clone(), new[] { true, true, true, true, true, false, true, true }, 100));
        Assert.Equal(9f, ReachMap.SelectThreshold((float[])values.Clone(), new[] { true, true, true, true, true, true, true, true }, 100));
    }

    [Fact]
    public void SelectThreshold_WithoutExclusions_EqualsSelectAtRank()
    {
        var random = new Random(131);
        foreach (var percent in new[] { 1f, 10f, 33.3f, 50f, 60f, 75f, 90f, 100f })
        {
            for (var trial = 0; trial < 200; trial++)
            {
                var n = random.Next(1, 40);
                var values = new float[n];
                for (var k = 0; k < n; k++)
                {
                    values[k] = random.Next(0, 5);
                }

                var expected = ReachMap.Select((float[])values.Clone(), ReachMap.Rank(n, percent));
                Assert.Equal(expected, ReachMap.SelectThreshold((float[])values.Clone(), new bool[n], percent));
            }
        }
    }

    // Oracle: sort, walk the ranks, accept the first rank whose unexcluded count reaches the share.
    private static float SortedScan(float[] values, bool[] flags, double percent)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(k => values[k]).ToArray();
        var excluded = 0;
        for (var r = 0; r < order.Length; r++)
        {
            excluded += flags[order[r]] ? 1 : 0;
            var needed = (int)Math.Ceiling((values.Length - excluded) * percent / 100.0 - 1e-9);
            if (r + 1 - excluded >= needed)
            {
                return values[order[r]];
            }
        }

        throw new InvalidOperationException("the largest value always passes");
    }

    [Fact]
    public void SelectThreshold_MatchesTheSortedScan_WithTiesAndExclusions()
    {
        var random = new Random(1131);
        for (var trial = 0; trial < 5000; trial++)
        {
            var n = random.Next(1, 14);
            var values = new float[n];
            var flags = new bool[n];
            for (var k = 0; k < n; k++)
            {
                values[k] = random.Next(0, 4);
                flags[k] = random.NextDouble() < 0.4;
            }

            var percent = new[] { 1, 25, 50, 60, 75, 89, 100 }[random.Next(7)];
            var expected = SortedScan(values, flags, percent);
            Assert.Equal(expected, ReachMap.SelectThreshold((float[])values.Clone(), (bool[])flags.Clone(), percent));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.SelectThreshold(Array.Empty<float>(), Array.Empty<bool>(), 50));
        Assert.Throws<ArgumentException>(() => ReachMap.SelectThreshold(new float[2], new bool[1], 50));
    }

    [Fact]
    public void RoundPercent_HalvesTheUnintendedShare()
    {
        Assert.Equal(100.0, ReachMap.RoundPercent(90f, 0));
        Assert.Equal(95.0, ReachMap.RoundPercent(90f, 1));
        Assert.Equal(90.0, ReachMap.RoundPercent(90f, 2));
        Assert.Equal(75.0, ReachMap.RoundPercent(50f, 1));
        Assert.Equal(100.0, ReachMap.RoundPercent(100f, 1));
        Assert.Equal(3, ReachMap.Rounds);
    }

    // Round 1 brings every floor cell to the model, so a position on the wall line has no intended
    // cell left to gain and keeps the wall top: the wall is not cut back.
    [Fact]
    public void StraightWall_IsPreserved_BecauseTheFloorIsReachedWithoutIt()
    {
        var (model, stock) = WallAtTen();
        var reach = ReachMap.Compute(model, stock, PlusProfile(), 0f);
        Assert.Equal(0f, reach[8, 5]);
        Assert.Equal(Wall, reach[9, 5]);
        Assert.Equal(Wall, reach[10, 5]);
        Assert.Equal(Wall, reach[11, 5]);

        stock[9, 4] = float.NaN;
        model[9, 6] = Wall;
        Assert.Equal(Wall, ReachMap.Compute(model, stock, PlusProfile(), 0f)[9, 5]);

        var tool = new ToolDefinition { CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };
        var profile = ToolProfile.Create(tool, Cell);
        var wide = ReachMap.Compute(WallAtTen().Model, WallAtTen().Stock, profile, 0f);
        var drop = HeightMapDilation.ComputeTipMap(WallAtTen().Model, profile);
        for (var j = 3; j < 17; j++)
        {
            Assert.Equal(0f, wide[3, j]);
            Assert.Equal(Wall, wide[4, j]);
            Assert.Equal(Wall, wide[9, j]);
        }

        for (var k = 0; k < wide.CellCount; k++)
        {
            Assert.Equal(MathF.Max(drop.Z[k], 0f), wide.Z[k]);
        }
    }

    // A ball edge stands dz above the tip, so the column beside the wall is not reached in round 1
    // (the wall raises its own position, the neighbours' edges stay above the floor) and its position
    // is admitted in round 2 with 3 intended of 4 counted cells; the wall top itself is never voted
    // down because every wall cell is reached through its own position: no dimple.
    [Fact]
    public void BallTip_DoesNotDimpleTheWallTop_AndReachesTheColumnBesideIt()
    {
        var (model, stock) = WallAtTen();
        var ball = ReachMap.Compute(model, stock, PlusProfile(TipType.Ball), 0f);
        Assert.Equal(0.5f, ToolProfile.BottomHeight(TipType.Ball, 0.5f, Cell), 4);
        Assert.Equal(0f, ball[8, 5]);
        Assert.Equal(0f, ball[9, 5]);
        Assert.Equal(Wall, ball[10, 5]);
        Assert.Equal(Wall, ball[12, 5]);
    }

    // Pocket at 0 (columns 0..7), a one-cell groove at -1 (column 8) the drop cutter cannot reach,
    // wall at 5 from column 9. A 2 mm tool (13 cells) centered on the groove sees 5 groove cells
    // unreached, 4 pocket cells reached and 4 wall cells: plain vote 9 of 13 (69 percent) at level
    // 0, discounted 5 of 9 (56 percent).
    [Fact]
    public void ReachedPocketCells_DoNotVoteForCuttingTheWall()
    {
        var model = new HeightMap(0, 0, Cell, 20, 20, Wall);
        var stock = new HeightMap(0, 0, Cell, 20, 20, Wall);
        for (var j = 0; j < 20; j++)
        {
            for (var i = 0; i < 8; i++)
            {
                model[i, j] = 0f;
            }

            model[8, j] = -1f;
        }

        var profile = ToolProfile.Create(new ToolDefinition { CutterDiameter = 2f, HeadDiameter = 4f, CutterLength = 10f }, Cell);
        Assert.Equal(13, profile.Offsets.Length);
        var at60 = ReachMap.Compute(model, stock, profile, -1f, 60f);
        var at50 = ReachMap.Compute(model, stock, profile, -1f, 50f);
        Assert.Equal(Wall, at60[8, 10]);
        Assert.Equal(0f, at50[8, 10]);
        // The plain vote at 60 percent would have entered the level: rank 7 of 13 lands on a pocket cell.
        var plain = new float[] { -1, -1, -1, -1, -1, 0, 0, 0, 0, Wall, Wall, Wall, Wall };
        Assert.Equal(0f, ReachMap.Select(plain, ReachMap.Rank(13, 60f)));
        // Away from the groove the pocket is at its floor and the wall top stands.
        Assert.Equal(0f, at60[3, 10]);
        Assert.Equal(Wall, at60[15, 10]);
    }

    [Fact]
    public void Floors_NeverRiseBetweenRounds_AndStayInsideTheBounds()
    {
        var (model, stock) = WallAtTen();
        model[12, 12] = 2f;
        model[5, 5] = 3f;
        var profile = ToolProfile.Create(new ToolDefinition { CutterDiameter = 2f, HeadDiameter = 4f, CutterLength = 10f }, Cell);
        var drop = ReachMap.Compute(model, stock, profile, 0f, 100f);
        foreach (var percent in new[] { 90f, 50f, 20f })
        {
            var reach = ReachMap.Compute(model, stock, profile, 0f, percent);
            for (var k = 0; k < reach.CellCount; k++)
            {
                Assert.True(reach.Z[k] <= drop.Z[k], $"cell {k} above the drop cutter at {percent} percent");
                Assert.True(reach.Z[k] >= 0f, $"cell {k} below the floor at {percent} percent");
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.Compute(model, stock, profile, 0f, 50f, -0.01f));
    }

    // The mark uses the tolerance: a plateau a hair below the round floor is reached, one a whole
    // step below is not, which decides whether its cells vote in the later rounds.
    [Fact]
    public void ReachedMark_UsesTheTolerance()
    {
        var (model, stock) = WallAtTen();
        // Column 9 sits 0.02 under the floor cells; with the plus tool no floor position reaches it.
        for (var j = 0; j < 20; j++)
        {
            model[9, j] = -0.02f;
        }

        var tight = ReachMap.Compute(model, stock, PlusProfile(), -1f, 50f, Slicer.LevelTolerance);
        var loose = ReachMap.Compute(model, stock, PlusProfile(), -1f, 50f, 0.05f);
        // Tight: column 9 is unreached, so position (9, 5) has 3 intended of 4 counted cells in
        // round 2 (75 percent) and descends to -0.02. Loose: column 9 counts as reached at 0, the
        // position has nothing to gain and keeps the wall top.
        Assert.Equal(-0.02f, tight[9, 5], 4);
        Assert.Equal(Wall, loose[9, 5]);
    }

    [Fact]
    public void Progress_ReportsEveryRowBlockOfEveryRound()
    {
        var (model, stock) = WallAtTen();
        var reports = new List<StepProgress>();
        ReachMap.Compute(model, stock, PlusProfile(), 0f, 50f, Slicer.LevelTolerance, new Recorder(reports.Add));
        var blocks = (model.Height + ReachMap.RowsPerBlock - 1) / ReachMap.RowsPerBlock;
        Assert.Equal(2, blocks);
        Assert.Equal(ReachMap.Rounds * blocks, reports.Count);
        Assert.Equal(1, reports[0].Step);
        Assert.Equal(2, reports[blocks].Step);
        Assert.Equal(3, reports[^1].Step);
        Assert.All(reports, r => Assert.Equal(ReachMap.Rounds, r.Steps));
        Assert.Equal((float)ReachMap.RowsPerBlock / model.Height / ReachMap.Rounds, reports[0].Fraction, 5);
        Assert.Equal(1f / ReachMap.Rounds, reports[blocks - 1].Fraction, 5);
        Assert.Equal(1f, reports[^1].Fraction, 5);
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).OrderBy(f => f)), "progress went backwards");

        // A percent of 100 repeats the drop cutter: the later rounds report once and change nothing.
        reports.Clear();
        var drop = ReachMap.Compute(model, stock, PlusProfile(), 0f, 100f, Slicer.LevelTolerance, new Recorder(reports.Add));
        Assert.Equal(blocks + 2, reports.Count);
        Assert.Equal(new[] { 2f / 3, 1f }, reports.Skip(blocks).Select(r => r.Fraction));
        Assert.Equal(HeightMapDilation.ComputeTipMap(model, PlusProfile()).Z, drop.Z);
    }

    // The rows of a block are computed in parallel; the result must not depend on it.
    [Fact]
    public void Compute_IsDeterministic()
    {
        var model = new HeightMap(0, 0, 0.1f, 120, 90, 0f);
        var stock = new HeightMap(0, 0, 0.1f, 120, 90, 5.1f);
        for (var k = 0; k < model.CellCount; k++)
        {
            model.Z[k] = (k * 7919) % 51 * 0.1f;
        }

        var profile = ToolProfile.Create(new ToolDefinition { CutterDiameter = 1.2f, HeadDiameter = 10f, CutterLength = 50f }, 0.1f);
        var first = ReachMap.Compute(model, stock, profile, 0f, 50f, 0.05f);
        for (var trial = 0; trial < 5; trial++)
        {
            Assert.Equal(first.Z, ReachMap.Compute(model, stock, profile, 0f, 50f, 0.05f).Z);
        }
    }

    private sealed class Recorder : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _report;

        public Recorder(Action<StepProgress> report) => _report = report;

        public void Report(StepProgress value) => _report(value);
    }
}
