using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class HeightMapDilationTests
{
    private const float CellSize = 0.5f;
    private const int Size = 40;
    private const int Spike = 20;
    private const float SpikeHeight = 10f;

    private static ToolDefinition Tool(TipType tip)
        => new() { TipType = tip, CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };

    private static HeightMap FlatMapWithSpike()
    {
        var map = new HeightMap(0, 0, CellSize, Size, Size, 0f);
        map[Spike, Spike] = SpikeHeight;
        return map;
    }

    [Fact]
    public void FlatTool_TurnsASpikeIntoAPlateauOfTheFootprintDiameter()
    {
        var tip = HeightMapDilation.ComputeTipMap(FlatMapWithSpike(), ToolProfile.Create(Tool(TipType.Flat), CellSize));
        Assert.True(tip.SameGridAs(FlatMapWithSpike()));
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                var dx = i - Spike;
                var dy = j - Spike;
                var withinCutter = CellSize * MathF.Sqrt(dx * dx + dy * dy) <= 3f + ToolProfile.RadiusTolerance;
                Assert.Equal(withinCutter ? SpikeHeight : 0f, tip[i, j]);
            }
        }

        // Along the row through the spike the plateau spans 13 cells: 6 on each side plus the spike.
        var row = Enumerable.Range(0, Size).Count(i => tip[i, Spike] == SpikeHeight);
        Assert.Equal(13, row);
    }

    [Fact]
    public void BallTool_FollowsTheBallProfileAroundTheSpike()
    {
        var tip = HeightMapDilation.ComputeTipMap(FlatMapWithSpike(), ToolProfile.Create(Tool(TipType.Ball), CellSize));
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                var dx = i - Spike;
                var dy = j - Spike;
                var d = CellSize * MathF.Sqrt(dx * dx + dy * dy);
                var expected = d <= 3f + ToolProfile.RadiusTolerance
                    ? SpikeHeight - ToolProfile.BottomHeight(TipType.Ball, 3f, d)
                    : 0f;
                Assert.Equal(expected, tip[i, j], 4);
            }
        }
    }

    [Fact]
    public void TipMap_IsNeverBelowTheModel()
    {
        var mesh = TestMeshes.BumpPlate();
        var model = MeshRasterizer.CreateGridFor(mesh.Bounds, CellSize, 0f);
        MeshRasterizer.Rasterize(mesh, model, 0f);
        foreach (var tipType in new[] { TipType.Flat, TipType.Ball })
        {
            var tip = HeightMapDilation.ComputeTipMap(model, ToolProfile.Create(Tool(tipType), CellSize));
            for (var j = 0; j < model.Height; j++)
            {
                for (var i = 0; i < model.Width; i++)
                {
                    Assert.True(tip[i, j] >= model[i, j] - 1e-4f, $"{tipType} tip {tip[i, j]} below model {model[i, j]} at ({i},{j})");
                }
            }

            Assert.Equal(model.Max(), tip.Max(), 4);
        }
    }

    [Fact]
    public void NaNCells_DoNotConstrainAndAllNaNFootprintStaysNaN()
    {
        var map = new HeightMap(0, 0, CellSize, Size, Size, float.NaN);
        map[Spike, Spike] = SpikeHeight;
        var tip = HeightMapDilation.ComputeTipMap(map, ToolProfile.Create(Tool(TipType.Flat), CellSize));
        Assert.Equal(SpikeHeight, tip[Spike, Spike]);
        Assert.Equal(SpikeHeight, tip[Spike + 6, Spike]);
        Assert.True(float.IsNaN(tip[Spike + 7, Spike]));
        Assert.True(float.IsNaN(tip[0, 0]));
    }

    [Fact]
    public void GridEdges_UseOnlyTheCellsThatExist()
    {
        var map = new HeightMap(0, 0, CellSize, 8, 8, 1f);
        map[0, 0] = 5f;
        var tip = HeightMapDilation.ComputeTipMap(map, ToolProfile.Create(Tool(TipType.Flat), CellSize));
        Assert.Equal(5f, tip[0, 0]);
        Assert.Equal(5f, tip[6, 0]);
        Assert.Equal(1f, tip[7, 0]);
        Assert.Equal(1f, tip[7, 7]);
    }
}
