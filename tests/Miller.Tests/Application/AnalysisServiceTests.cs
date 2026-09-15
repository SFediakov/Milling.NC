using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

public sealed class AnalysisServiceTests
{
    // 8 x 8 x 3 box standing on the floor of a 14 x 14 x 3 stock, cut with a 2 mm tool.
    private static PipelineResult Result()
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 2;
        project.Tool.HeadDiameter = 4;
        project.Parameters.Stepover = 1;
        project.Stock.SizeX = 14;
        project.Stock.SizeY = 14;
        project.Stock.SizeZ = 3;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(8, 8, 3) }, null, CancellationToken.None);
    }

    [Fact]
    public async Task Analyze_LeavesThePipelineStockIntact_AndFindsNoGouge()
    {
        var result = Result();
        var analysis = await new AnalysisService().AnalyzeAsync(result, CancellationToken.None);
        Assert.Equal(0, analysis.GougeCells);
        Assert.True(analysis.OkCells > 0, "the box top matches the model");
        Assert.NotSame(result.Stock.Map, analysis.FinalStock);
        Assert.All(result.Stock.Map.Z, z => Assert.Equal(result.Stock.StockTop, z));
        Assert.Contains(analysis.FinalStock.Z, z => z < result.Stock.StockTop);
        Assert.Equal(result.Model.CellCount, analysis.Map.Categories.Length);
        Assert.Equal(analysis.OkCells + analysis.RestCells + analysis.GougeCells + analysis.NoModelCells, result.Model.CellCount);
    }

    [Fact]
    public async Task Uncuttable_OfABoxOnTheFloor_HasNoOverhang()
    {
        var result = Result();
        var uncuttable = await new AnalysisService().UncuttableAsync(result, CancellationToken.None);
        Assert.Equal(0, uncuttable.OverhangCells);
        Assert.Equal(result.Model.Width, uncuttable.CornerLimited.GetLength(0));
    }

    [Fact]
    public async Task Analyze_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisService().AnalyzeAsync(Result(), cts.Token));
    }
}
