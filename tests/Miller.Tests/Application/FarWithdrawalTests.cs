using Miller.Application.Services;
using Miller.Core.Generation;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-164: a collision's place and everything within 1 mm of it in XY leave the far stepdown for every
// later pass and are milled with the normal stepdown; a later pass may take only the first pass's far
// positions that were never withdrawn.
public sealed class FarWithdrawalTests
{
    private const float WithdrawRadius = 1f;

    // One place at the centre of 21 x 21 cells of 0.25 mm: the 49 cells with dx^2 + dy^2 <= 16 lie within
    // 1 mm and leave; a cell 1.03 mm away stays; a second withdrawal of the same place clears nothing.
    [Fact]
    public void Withdraw_ClearsTheAllowedCellsWithinTheRadius_Once()
    {
        var places = new bool[21, 21];
        places[10, 10] = true;
        var allowed = new bool[21, 21];
        for (var j = 0; j < 21; j++)
        {
            for (var i = 0; i < 21; i++)
            {
                allowed[i, j] = true;
            }
        }

        allowed[0, 0] = false;
        Assert.Equal(49, FarWithdrawal.Withdraw(places, 0.25f, WithdrawRadius, allowed));
        for (var j = 0; j < 21; j++)
        {
            for (var i = 0; i < 21; i++)
            {
                var within = (i - 10) * (i - 10) + (j - 10) * (j - 10) <= 16;
                Assert.Equal(!within && !(i == 0 && j == 0), allowed[i, j]);
            }
        }

        Assert.False(allowed[14, 10]);
        Assert.True(allowed[14, 11]);
        Assert.Equal(0, FarWithdrawal.Withdraw(places, 0.25f, WithdrawRadius, allowed));
        Assert.Equal(0, FarWithdrawal.Withdraw(new bool[21, 21], 0.25f, WithdrawRadius, allowed));
    }

    [Fact]
    public void Withdraw_RejectsANegativeRadius_AMismatchedMask_AndABadCell()
    {
        Assert.Throws<ArgumentException>(() => FarWithdrawal.Withdraw(new bool[4, 4], 0.25f, -1f, new bool[4, 4]));
        Assert.Throws<ArgumentException>(() => FarWithdrawal.Withdraw(new bool[4, 4], 0.25f, float.NaN, new bool[4, 4]));
        Assert.Throws<ArgumentException>(() => FarWithdrawal.Withdraw(new bool[4, 4], 0f, 1f, new bool[4, 4]));
        Assert.Throws<ArgumentException>(() => FarWithdrawal.Withdraw(new bool[4, 4], 0.25f, 1f, new bool[4, 5]));
    }

    // A 2 mm cutter under a 7 to 10 mm frustum beside a box taller than the cutter length in a 9 mm stock:
    // the first pass collides beside the walls, and with a small stepover the far region of the stock
    // around the box comes within a millimetre of those collisions.
    private static PipelineResult Run(CollisionMode mode, float farStepdown, float boxHeight, float stepover)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.RoutingStrategyId = ZLayerByLayerStrategy.StrategyId;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterLength = 2f;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7f;
        project.Tool.HeadTopDiameter = 10f;
        project.Tool.HeadLength = 10f;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.Stepover = stepover;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, boxHeight) }, null, CancellationToken.None);
    }

    private static int Count(bool[,] mask)
    {
        var n = 0;
        foreach (var b in mask)
        {
            n += b ? 1 : 0;
        }

        return n;
    }

    // Recursion on a 4 mm box (stepover 1, far stepdown 4) ends at no collision; one run on a 6 mm box
    // (stepover 2, far stepdown 8) keeps its third pass.
    [Theory]
    [InlineData(CollisionMode.Recursion, 4f, 4f, 1f)]
    [InlineData(CollisionMode.OneRun, 8f, 6f, 2f)]
    public void CollisionsWithdrawTheFarStepdown_AndTheFarAreaNeverGrows(CollisionMode mode, float farStepdown, float boxHeight, float stepover)
    {
        var result = Run(mode, farStepdown, boxHeight, stepover);
        var passes = result.PassCollisions;
        var areas = result.FarStepdown;
        Assert.Equal(result.Passes, passes.Count);
        Assert.True(passes[0].EnteredCells > 0, "the fixture must collide in its first pass");
        Assert.True(passes[0].FarCells > 0, "the first pass must mill a far region");
        Assert.True(passes[0].WithdrawnCells > 0, $"no far position within {WithdrawRadius} mm of the first pass's collisions");
        Assert.True(result.Passes >= 2, "a withdrawal must start another pass");
        Assert.Equal(passes[0].FarCells, Count(areas.FirstArea));

        // Every later pass mills at most the first pass's far area less what was withdrawn before it.
        var withdrawnBefore = 0;
        for (var k = 1; k < passes.Count; k++)
        {
            withdrawnBefore += passes[k - 1].WithdrawnCells;
            Assert.True(passes[k].FarCells <= passes[0].FarCells - withdrawnBefore, $"pass {k + 1}: {passes[k].FarCells} far positions, {passes[0].FarCells - withdrawnBefore} allowed");
        }

        // The kept pass (fewest entered cells, the first of equals) ran without everything withdrawn before it.
        var kept = 0;
        for (var k = 1; k < passes.Count; k++)
        {
            if (passes[k].EnteredCells < passes[kept].EnteredCells)
            {
                kept = k;
            }
        }

        Assert.Equal(passes[kept].FarCells, Count(areas.Milled));
        Assert.Equal(passes.Take(kept).Sum(p => p.WithdrawnCells), Count(areas.Withdrawn));
        var width = areas.FirstArea.GetLength(0);
        var height = areas.FirstArea.GetLength(1);
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                Assert.False(areas.Milled[i, j] && areas.Withdrawn[i, j], $"({i}, {j}) is withdrawn and milled with the far stepdown");
                Assert.True(!areas.Milled[i, j] || areas.FirstArea[i, j], $"({i}, {j}) is milled with the far stepdown outside the first pass's area");
                Assert.True(!areas.Withdrawn[i, j] || areas.FirstArea[i, j], $"({i}, {j}) is withdrawn outside the first pass's area");
            }
        }

        Assert.True(kept > 0 && Count(areas.Withdrawn) > 0, $"kept pass {kept + 1}, {Count(areas.Withdrawn)} withdrawn");
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
    }

    // Without a far stepdown nothing is withdrawn and one run stays one pass.
    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void WithoutAFarStepdown_NothingIsWithdrawn(CollisionMode mode)
    {
        var result = Run(mode, 0f, 6f, 2f);
        Assert.All(result.PassCollisions, p => Assert.Equal((0, 0), (p.FarCells, p.WithdrawnCells)));
        Assert.Equal(0, Count(result.FarStepdown.FirstArea) + Count(result.FarStepdown.Milled) + Count(result.FarStepdown.Withdrawn));
        if (mode == CollisionMode.OneRun)
        {
            Assert.Equal(1, result.Passes);
        }
    }
}
