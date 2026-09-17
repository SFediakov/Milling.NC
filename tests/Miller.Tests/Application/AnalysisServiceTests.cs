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

    // The reach map decides by majority, so the tool axis reaches the box wall line and the wall is
    // cut back by one cutter radius: those cells are reported as gouge, the box top stays Ok.
    [Fact]
    public async Task Analyze_LeavesThePipelineStockIntact_AndReportsTheWallGouge()
    {
        var result = Result();
        var analysis = await new AnalysisService().AnalyzeAsync(result, CancellationToken.None);
        Assert.True(analysis.GougeCells > 0, "the majority rule cuts the box walls back");
        var model = result.Model;
        var reach = result.Parameters.CellSize * MathF.Ceiling(result.Profile.Tool.CutterRadius / result.Parameters.CellSize) + result.Parameters.CellSize;
        for (var k = 0; k < model.CellCount; k++)
        {
            if (analysis.Map.Categories[k] != Miller.Core.Analysis.CellCategory.Gouge)
            {
                continue;
            }

            var i = k % model.Width;
            var j = k / model.Width;
            var nearWall = false;
            var cells = (int)MathF.Ceiling(reach / model.CellSize);
            for (var dj = -cells; dj <= cells && !nearWall; dj++)
            {
                for (var di = -cells; di <= cells; di++)
                {
                    var ii = i + di;
                    var jj = j + dj;
                    if (model.InBounds(ii, jj) && model[ii, jj] <= result.Floor + 1e-4f && model.CellSize * MathF.Sqrt(di * di + dj * dj) <= reach)
                    {
                        nearWall = true;
                        break;
                    }
                }
            }

            Assert.True(nearWall, $"gouge at ({i}, {j}) farther than the cutter radius from the wall");
        }

        var (ci, cj) = model.CellOf(7f, 7f);
        Assert.Equal(Miller.Core.Analysis.CellCategory.Ok, analysis.Map.Categories[model.Index(ci, cj)]);
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
