using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.Analysis;

namespace Miller.App.ViewModels;

// Analyze command, result numbers and the viewport switch between the simulation stock, the final
// model colored by deviation category and the uncuttable overlay on top of it.
public sealed partial class AnalysisViewModel : ViewModelBase
{
    public const string NoResultText = "Generate a toolpath, then analyze the final model.";
    public const string AnalyzingText = "Simulating the whole toolpath and comparing it with the model...";

    private readonly AnalysisService _analysis;
    private readonly ViewportViewModel _viewport;
    private readonly SimulationService _simulation;
    private PipelineResult? _pipeline;

    [ObservableProperty]
    private AnalysisResult? _result;

    [ObservableProperty]
    private UncuttableResult? _uncuttable;

    [ObservableProperty]
    private bool _showFinalModel;

    [ObservableProperty]
    private bool _showUncuttable;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _summaryText = NoResultText;

    public AnalysisViewModel(AnalysisService analysis, ViewportViewModel viewport, SimulationService simulation)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
    }

    public bool HasResult => Result is not null;

    // A new pipeline result (or none) clears the analysis and returns the viewport to the stock view.
    public void SetPipeline(PipelineResult? pipeline)
    {
        _pipeline = pipeline;
        Result = null;
        Uncuttable = null;
        ShowUncuttable = false;
        ShowFinalModel = false;
        SummaryText = NoResultText;
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private bool CanAnalyze => _pipeline is not null && !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var pipeline = _pipeline!;
        IsAnalyzing = true;
        SummaryText = AnalyzingText;
        AnalyzeCommand.NotifyCanExecuteChanged();
        try
        {
            Result = await _analysis.AnalyzeAsync(pipeline, CancellationToken.None);
            Uncuttable = await _analysis.UncuttableAsync(pipeline, CancellationToken.None);
            SummaryText = Summarize(Result, Uncuttable);
            OnPropertyChanged(nameof(HasResult));
            ShowFinalModel = true;
            ApplyView();
        }
        finally
        {
            IsAnalyzing = false;
            AnalyzeCommand.NotifyCanExecuteChanged();
        }
    }

    // Final model with categories, optionally overlaid with the uncuttable reasons; otherwise the
    // simulation stock without categories.
    public void ApplyView()
    {
        if (Result is null || !ShowFinalModel)
        {
            if (_simulation.IsLoaded)
            {
                _viewport.SetStockMap(_simulation.Stock, _simulation.Result!.Stock.StockBottom);
            }

            return;
        }

        _viewport.SetStockMap(Result.FinalStock, _pipeline!.Stock.StockBottom);
        _viewport.SetCategories(Compose());
    }

    public CellCategory[] Compose()
    {
        var categories = (CellCategory[])Result!.Map.Categories.Clone();
        if (!ShowUncuttable || Uncuttable is null)
        {
            return categories;
        }

        var map = Result.Map.Values;
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var k = map.Index(i, j);
                if (Uncuttable.Overhang[i, j])
                {
                    categories[k] = CellCategory.Overhang;
                }
                else if (Uncuttable.HeadLimited[i, j])
                {
                    categories[k] = CellCategory.HeadLimited;
                }
                else if (Uncuttable.CornerLimited[i, j])
                {
                    categories[k] = CellCategory.CornerLimited;
                }
            }
        }

        return categories;
    }

    public static string Summarize(AnalysisResult result, UncuttableResult? uncuttable)
    {
        var text = new StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Ok: {result.OkCells} cells\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Rest material: {result.RestCells} cells, {result.RestArea:0.0} mm2, {result.RestVolume:0.00} mm3\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Gouge: {result.GougeCells} cells, {result.GougeArea:0.0} mm2, {result.GougeVolume:0.00} mm3\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"No model (floor): {result.NoModelCells} cells\n"));
        if (uncuttable is not null)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Overhang: {uncuttable.OverhangCells} cells, head limited: {uncuttable.HeadLimitedCells}, corner limited: {uncuttable.CornerLimitedCells}"));
        }

        return text.ToString().TrimEnd('\n');
    }

    partial void OnShowFinalModelChanged(bool value) => ApplyView();

    partial void OnShowUncuttableChanged(bool value) => ApplyView();
}
