using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Xunit;

namespace Miller.Tests.Core.Slicing;

public sealed class SlicerTests
{
    private const int Size = 20;

    private static HeightMap Map(float fill) => new(0, 0, 1f, Size, Size, fill);

    private static CuttingParameters Parameters(float stepdown) => new() { Stepdown = stepdown };

    [Theory]
    [InlineData(3.5f, 4f)]
    [InlineData(2.5f, 3f)]
    [InlineData(3f, 3f)]
    [InlineData(3.00001f, 3f)]
    [InlineData(5f, 5f)]
    [InlineData(6f, 6f)]
    [InlineData(0.2f, 1f)]
    [InlineData(float.NaN, float.NaN)]
    public void CeilToLevel_LiftsToTheLevelStandingAboveTheSurface(float z, float expected)
    {
        Assert.Equal(expected, Slicer.CeilToLevel(z, 5f, 1f));
    }

    [Fact]
    public void Levels_StepDownFromTheStockTopAndClampToTheLowestTip()
    {
        Assert.Equal(new[] { 3f, 1f, 0f }, Slicer.Levels(5f, 0f, 2f));
        Assert.Equal(new[] { 3f, 1f, -1f, -2.5f }, Slicer.Levels(5f, -2.5f, 2f));
        Assert.Equal(new[] { 0f }, Slicer.Levels(5f, 0f, 10f));
        Assert.Empty(Slicer.Levels(5f, 5f, 2f));
        Assert.Empty(Slicer.Levels(5f, 6f, 2f));
    }

    [Fact]
    public void Plan_ForAPocket_HasLevelsWhoseMasksShrinkWithDepth()
    {
        // A 20 x 20 stock at 5 with a stepped tip map: outer ring at 4, middle ring at 2, center at 0.
        var tip = Map(4f);
        for (var j = 4; j < 16; j++)
        {
            for (var i = 4; i < 16; i++)
            {
                tip[i, j] = i >= 8 && i < 12 && j >= 8 && j < 12 ? 0f : 2f;
            }
        }

        var plan = Slicer.Build(tip, Map(5f), Parameters(2f));
        Assert.Equal(3, plan.Levels);
        Assert.Equal(0f, plan.LowestLevel);
        Assert.Equal(new[] { 3f, 1f, 0f }, plan.Steps.Select(s => s.Level));

        var counts = plan.Steps.Select(s => s.MaskCount).ToArray();
        Assert.Equal(new[] { 144, 16, 16 }, counts);
        for (var k = 1; k < counts.Length; k++)
        {
            Assert.True(counts[k] <= counts[k - 1]);
        }

        Assert.Equal(Size * Size, plan.Coverage.Cast<bool>().Count(b => b));
    }

    [Fact]
    public void Mask_RequiresTipAtOrBelowTheLevelAndStockAboveIt()
    {
        var tip = Map(0f);
        var stock = Map(5f);
        stock[0, 0] = 1f;          // already cut below the level 3
        stock[1, 0] = float.NaN;   // outside a cylinder
        tip[2, 0] = 3f;            // tip exactly at the level: allowed
        tip[3, 0] = 3.5f;          // tip above the level: not allowed
        var plan = Slicer.Build(tip, stock, Parameters(2f));
        var level3 = plan.Steps.First();
        Assert.Equal(3f, level3.Level);
        Assert.False(level3.Mask[0, 0]);
        Assert.False(level3.Mask[1, 0]);
        Assert.True(level3.Mask[2, 0]);
        Assert.False(level3.Mask[3, 0]);
        Assert.True(level3.Mask[4, 0]);
    }

    [Fact]
    public void FlatTipAtTheStockTop_GivesNoLevelsButFullCoverage()
    {
        var plan = Slicer.Build(Map(5f), Map(5f), Parameters(2f));
        Assert.Equal(0, plan.Levels);
        Assert.Empty(plan.Steps);
        Assert.Equal(Size * Size, plan.Coverage.Cast<bool>().Count(b => b));
        Assert.Equal(5f, plan.LowestLevel);
    }

    [Fact]
    public void BadInput_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Slicer.Build(Map(0f), Map(5f), Parameters(0f)));
        Assert.Throws<ArgumentException>(() => Slicer.Build(Map(0f), new HeightMap(0, 0, 1f, Size + 1, Size, 5f), Parameters(2f)));
        Assert.Throws<ArgumentException>(() => Slicer.Build(Map(float.NaN), Map(5f), Parameters(2f)));
    }
}
