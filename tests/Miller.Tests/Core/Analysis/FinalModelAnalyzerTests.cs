using Miller.Core.Analysis;
using Miller.Core.HeightMaps;
using Xunit;

namespace Miller.Tests.Core.Analysis;

public sealed class FinalModelAnalyzerTests
{
    private const float Cell = 0.5f;
    private const float Tolerance = 0.05f;
    private const float ModelHeight = 2f;

    // 4 x 4 grid: the left half holds a 2 mm high model, the right half is floor (0).
    private static HeightMap Model()
    {
        var model = new HeightMap(0, 0, Cell, 4, 4, 0f);
        for (var j = 0; j < 4; j++)
        {
            for (var i = 0; i < 2; i++)
            {
                model[i, j] = ModelHeight;
            }
        }

        return model;
    }

    private static HeightMap StockAt(float offset)
    {
        var stock = Model();
        for (var k = 0; k < stock.Z.Length; k++)
        {
            stock.Z[k] += offset;
        }

        return stock;
    }

    [Fact]
    public void IdenticalMaps_AreAllOk_AndFloorCellsAreNoModel()
    {
        var result = FinalModelAnalyzer.Analyze(Model(), Model(), 0f, Tolerance);
        Assert.Equal(8, result.OkCells);
        Assert.Equal(8, result.NoModelCells);
        Assert.Equal(0, result.RestCells);
        Assert.Equal(0, result.GougeCells);
        Assert.Equal(0f, result.RestVolume);
        Assert.Equal(Cell * Cell, result.CellArea);
        Assert.Equal(8, result.Map.Count(CellCategory.Ok));
        Assert.Equal(8, result.Map.Count(CellCategory.NoModel));
        Assert.True(float.IsNaN(result.Map.Values[3, 3]));
        Assert.Equal(0f, result.Map.Values[0, 0]);
    }

    [Fact]
    public void StockAboveTheModel_IsRestMaterialWithItsVolume()
    {
        var result = FinalModelAnalyzer.Analyze(StockAt(0.3f), Model(), 0f, Tolerance);
        Assert.Equal(8, result.RestCells);
        Assert.Equal(0, result.OkCells);
        Assert.Equal(8 * 0.3f * Cell * Cell, result.RestVolume, 4);
        Assert.Equal(8 * Cell * Cell, result.RestArea, 4);
        Assert.Equal(CellCategory.RestMaterial, result.Map.Categories[result.Map.Values.Index(1, 1)]);
    }

    [Fact]
    public void StockBelowTheModel_IsGouge_AndWithinToleranceIsOk()
    {
        var gouged = FinalModelAnalyzer.Analyze(StockAt(-0.2f), Model(), 0f, Tolerance);
        Assert.Equal(8, gouged.GougeCells);
        Assert.Equal(8 * 0.2f * Cell * Cell, gouged.GougeVolume, 4);

        var close = FinalModelAnalyzer.Analyze(StockAt(0.04f), Model(), 0f, Tolerance);
        Assert.Equal(8, close.OkCells);
        Assert.Equal(0, close.RestCells);
    }

    [Fact]
    public void StockCutAway_CountsAsTheFloor()
    {
        var stock = Model();
        stock[0, 0] = float.NaN;
        var result = FinalModelAnalyzer.Analyze(stock, Model(), 0f, Tolerance);
        Assert.Equal(1, result.GougeCells);
        Assert.Equal(ModelHeight * Cell * Cell, result.GougeVolume, 4);
        Assert.Equal(-ModelHeight, result.Map.Values[0, 0]);
    }

    [Fact]
    public void MismatchedGridOrNegativeTolerance_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => FinalModelAnalyzer.Analyze(new HeightMap(0, 0, Cell, 3, 3, 0f), Model(), 0f, Tolerance));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinalModelAnalyzer.Analyze(Model(), Model(), 0f, -1f));
        Assert.Throws<ArgumentException>(() => new DeviationMap(Model(), new CellCategory[3]));
    }
}
