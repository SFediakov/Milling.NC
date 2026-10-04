using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.Analysis;
using Miller.Core.Simulation;

namespace Miller.App.ViewModels;

// Analyze command, result numbers and the viewport switch between the simulation stock, the final
// model colored by deviation category, the uncuttable overlay on top of it and the collision marks
// of the generation's collision check on top of everything.
public sealed partial class AnalysisViewModel : ViewModelBase
{
    public const string NoResultText = "Generate a toolpath, then analyze the final model.";
    public const string AnalyzingText = "Simulating the whole toolpath and comparing it with the model...";

    private readonly AnalysisService _analysis;
    private readonly ViewportViewModel _viewport;
    private readonly SimulationService _simulation;
    private PipelineResult? _pipeline;
    private CollisionReport? _collisions;

    [ObservableProperty]
    private AnalysisResult? _result;

    [ObservableProperty]
    private UncuttableResult? _uncuttable;

    [ObservableProperty]
    private bool _showFinalModel;

    [ObservableProperty]
    private bool _showUncuttable;

    [ObservableProperty]
    private bool _showCollisions = true;

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

    public CollisionReport? Collisions => _collisions;

    // A new pipeline result (or none) clears the analysis and returns the viewport to the stock view.
    // The collision report belongs to that result; null when there is none or its check failed.
    public void SetPipeline(PipelineResult? pipeline, CollisionReport? collisions)
    {
        if (collisions is not null && (pipeline is null || collisions.Contacts.Length != pipeline.Model.CellCount))
        {
            throw new ArgumentException("The collision report does not belong to the pipeline result.", nameof(collisions));
        }

        _pipeline = pipeline;
        _collisions = collisions;
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
            SummaryText = Summarize(Result, Uncuttable, _collisions);
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
        var map = Result.Map.Values;
        if (ShowUncuttable && Uncuttable is not null)
        {
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
        }

        if (ShowCollisions && _collisions is not null)
        {
            for (var k = 0; k < categories.Length; k++)
            {
                categories[k] = _collisions.Contacts[k] switch
                {
                    CollisionContact.Model => CellCategory.CollisionModel,
                    CollisionContact.Stock => CellCategory.CollisionStock,
                    _ => categories[k],
                };
            }
        }

        return categories;
    }

    public static string Summarize(AnalysisResult result, UncuttableResult? uncuttable, CollisionReport? collisions = null)
    {
        var text = new StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Ok: {result.OkCells} cells\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Rest material: {result.RestCells} cells, {result.RestArea:0.0} mm2, {result.RestVolume:0.00} mm3\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Gouge: {result.GougeCells} cells, {result.GougeArea:0.0} mm2, {result.GougeVolume:0.00} mm3\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"No model (floor): {result.NoModelCells} cells\n"));
        if (uncuttable is not null)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Overhang: {uncuttable.OverhangCells} cells, head limited: {uncuttable.HeadLimitedCells}, corner limited: {uncuttable.CornerLimitedCells}\n"));
        }

        text.Append(collisions is null
            ? "Collisions: not checked"
            : string.Create(CultureInfo.InvariantCulture, $"Collisions: {collisions.Events.Count} (model: {collisions.Cells(CollisionContact.Model)} cells, stock: {collisions.Cells(CollisionContact.Stock)} cells)"));

        return text.ToString().TrimEnd('\n');
    }

    partial void OnShowFinalModelChanged(bool value) => ApplyView();

    partial void OnShowUncuttableChanged(bool value) => ApplyView();

    partial void OnShowCollisionsChanged(bool value) => ApplyView();
}
