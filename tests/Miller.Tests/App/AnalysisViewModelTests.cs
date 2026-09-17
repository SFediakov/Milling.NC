using Miller.App.ViewModels;
using Miller.Core.Analysis;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class AnalysisViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-analysis-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<MainWindowViewModel> GeneratedAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        var stl = Path.Combine(_root, "box.stl");
        File.WriteAllText(stl, TestMeshes.AsciiCubeText());
        _dialogs.OpenResults.Enqueue(stl);
        await vm.OpenStlCommand.ExecuteAsync(null);
        vm.Stock.SizeX = 10;
        vm.Stock.SizeY = 10;
        vm.Stock.SizeZ = 3;
        vm.Cutting.CellSize = 0.5f;
        await vm.GenerateCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Analyze_ShowsTheFinalModelWithCategories_AndTogglesBackToTheStock()
    {
        var vm = await GeneratedAsync();
        var analysis = vm.Analysis;
        Assert.False(analysis.HasResult);
        Assert.Equal(AnalysisViewModel.NoResultText, analysis.SummaryText);
        Assert.True(analysis.AnalyzeCommand.CanExecute(null));

        await analysis.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(analysis.HasResult);
        Assert.True(analysis.ShowFinalModel);
        Assert.Same(analysis.Result!.FinalStock, vm.Viewport.StockMap);
        Assert.NotNull(vm.Viewport.StockCategories);
        Assert.Equal(vm.Viewport.StockMap!.CellCount, vm.Viewport.StockCategories!.Length);
        Assert.Contains("Rest material", analysis.SummaryText);
        Assert.Contains("Overhang:", analysis.SummaryText);

        analysis.ShowFinalModel = false;
        Assert.Same(vm.Simulation.Stock, vm.Viewport.StockMap);
        Assert.Null(vm.Viewport.StockCategories);

        analysis.ShowFinalModel = true;
        analysis.ShowUncuttable = true;
        Assert.Same(analysis.Result.FinalStock, vm.Viewport.StockMap);
        Assert.Equal(analysis.Compose(), vm.Viewport.StockCategories);
        Assert.Contains(analysis.Compose(), c => c != CellCategory.Overhang);

        // Playing the simulation returns the viewport to the stock being cut.
        vm.SimulationPanel.PlayCommand.Execute(null);
        Assert.False(analysis.ShowFinalModel);
        Assert.Same(vm.Simulation.Stock, vm.Viewport.StockMap);

        vm.NewProjectCommand.Execute(null);
        Assert.False(analysis.HasResult);
        Assert.False(analysis.AnalyzeCommand.CanExecute(null));
    }
}
