using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class ToolProfileTests
{
    private const float CellSize = 0.5f;

    private static ToolDefinition Tool(TipType tip, float cutterDiameter = 6f, float headDiameter = 10f)
        => new() { TipType = tip, CutterDiameter = cutterDiameter, HeadDiameter = headDiameter, CutterLength = 20f };

    private static float Distance(ProfileOffset o) => CellSize * MathF.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy);

    [Fact]
    public void FlatTool_HasZeroBottomHeightInsideTheRadius()
    {
        var profile = ToolProfile.Create(Tool(TipType.Flat), CellSize);
        Assert.NotEmpty(profile.Offsets);
        Assert.All(profile.Offsets, o => Assert.Equal(0f, o.Dz));
        Assert.All(profile.Offsets, o => Assert.True(Distance(o) <= 3f + ToolProfile.RadiusTolerance));
        Assert.Equal(6, profile.RadiusCells);
        Assert.Equal(10, profile.HeadRadiusCells);
    }

    [Fact]
    public void Footprint_ContainsExactlyTheCellsWithinTheCutterRadius()
    {
        var profile = ToolProfile.Create(Tool(TipType.Flat), CellSize);
        var set = profile.Offsets.Select(o => (o.Dx, o.Dy)).ToHashSet();
        Assert.Contains((0, 0), set);
        Assert.Contains((6, 0), set);   // d = 3.0, on the edge
        Assert.Contains((4, 4), set);   // d = 2.83
        Assert.DoesNotContain((7, 0), set);
        Assert.DoesNotContain((5, 4), set); // d = 3.2

        var expected = 0;
        for (var dy = -6; dy <= 6; dy++)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                if (dx * dx + dy * dy <= 36) expected++;
            }
        }

        Assert.Equal(expected, profile.Offsets.Length);
    }

    [Fact]
    public void BallTool_RisesFromZeroAtTheAxisToTheRadiusAtTheEdge()
    {
        var profile = ToolProfile.Create(Tool(TipType.Ball), CellSize);
        var byOffset = profile.Offsets.ToDictionary(o => (o.Dx, o.Dy), o => o.Dz);
        Assert.Equal(0f, byOffset[(0, 0)]);
        Assert.Equal(3f, byOffset[(6, 0)], 4);
        Assert.Equal(3f - MathF.Sqrt(9f - 2.25f), byOffset[(3, 0)], 4);
        Assert.All(profile.Offsets, o => Assert.InRange(o.Dz, 0f, 3f + 1e-5f));
    }

    [Fact]
    public void Annulus_CoversTheHeadRingOnly()
    {
        var profile = ToolProfile.Create(Tool(TipType.Flat), CellSize);
        Assert.NotEmpty(profile.AnnulusOffsets);
        Assert.All(profile.AnnulusOffsets, o =>
        {
            var d = Distance(o);
            Assert.True(d > 3f, $"({o.Dx},{o.Dy}) d={d} is under the cutter");
            Assert.True(d <= 5f + ToolProfile.RadiusTolerance, $"({o.Dx},{o.Dy}) d={d} is outside the head");
            Assert.Equal(0f, o.Dz);
        });
        var set = profile.AnnulusOffsets.Select(o => (o.Dx, o.Dy)).ToHashSet();
        Assert.Contains((7, 0), set);
        Assert.Contains((10, 0), set);
        Assert.DoesNotContain((6, 0), set);
        Assert.DoesNotContain((11, 0), set);
        Assert.Empty(set.Intersect(profile.Offsets.Select(o => (o.Dx, o.Dy))));
    }

    [Fact]
    public void CoarseGrid_KeepsTheAxisCellOnly()
    {
        var profile = ToolProfile.Create(Tool(TipType.Ball), 10f);
        var only = Assert.Single(profile.Offsets);
        Assert.Equal(new ProfileOffset(0, 0, 0f), only);
        Assert.Empty(profile.AnnulusOffsets);
        Assert.Equal(1, profile.RadiusCells);
    }

    [Fact]
    public void Create_RejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolProfile.Create(Tool(TipType.Flat), 0f));
        Assert.Throws<ArgumentException>(() => ToolProfile.Create(Tool(TipType.Flat, cutterDiameter: 0f), CellSize));
    }

    [Fact]
    public void BottomHeight_ClampsBeyondTheBallRadius()
    {
        Assert.Equal(0f, ToolProfile.BottomHeight(TipType.Flat, 3f, 2f));
        Assert.Equal(3f, ToolProfile.BottomHeight(TipType.Ball, 3f, 4f));
        Assert.Equal(0f, ToolProfile.BottomHeight(TipType.Ball, 3f, 0f));
    }
}
