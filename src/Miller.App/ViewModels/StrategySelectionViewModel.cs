using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.App.ViewModels;

// Routing strategy and post-processor choice from the registries, the cut scope, the generate and
// cancel commands of the main view model, and the statistics of the last run.
public sealed class StrategySelectionViewModel : SettingsViewModelBase
{
    public const string NoToolpathText = "No toolpath yet.";

    public StrategySelectionViewModel(ProjectService project, IAsyncRelayCommand generate, IRelayCommand cancel)
        : base(project, "Strategy.")
    {
        GenerateCommand = generate ?? throw new ArgumentNullException(nameof(generate));
        CancelCommand = cancel ?? throw new ArgumentNullException(nameof(cancel));
    }

    public static IReadOnlyList<IToolpathStrategy> Strategies { get; } = StrategyRegistry.All;

    public IReadOnlyList<IPostProcessor> PostProcessors { get; } = PostProcessorRegistry.All;

    public static IReadOnlyList<CutScope> CutScopes { get; } = Enum.GetValues<CutScope>();

    public IAsyncRelayCommand GenerateCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IToolpathStrategy? Strategy
    {
        get => Strategies.FirstOrDefault(s => s.Id == Current.RoutingStrategyId);
        set
        {
            if (value is not null)
            {
                Edit(p => p.RoutingStrategyId = value.Id);
            }
        }
    }

    public IPostProcessor? PostProcessor
    {
        get => PostProcessors.FirstOrDefault(p => p.Id == Current.PostProcessorId);
        set
        {
            if (value is not null)
            {
                Edit(p => p.PostProcessorId = value.Id);
            }
        }
    }

    public CutScope CutScope
    {
        get => Current.CutScope;
        set
        {
            Edit(p => p.CutScope = value);
            OnPropertyChanged(nameof(IsSeparation));
        }
    }

    public bool IsSeparation => Current.CutScope == CutScope.Separation;

    public float MinIslandVolume { get => Current.MinIslandVolume; set => Edit(p => p.MinIslandVolume = value); }

    public string? MinIslandVolumeError => ErrorFor("Strategy.MinIslandVolume");

    public ToolpathStatistics? Statistics { get; private set; }

    public SlicePlan? Plan { get; private set; }

    public string StatisticsText
    {
        get
        {
            if (Statistics is null || Plan is null)
            {
                return NoToolpathText;
            }

            var text = new StringBuilder();
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Segments: {Statistics.SegmentCount}\n"));
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Feed: {Statistics.FeedLength:0.0} mm, plunge: {Statistics.PlungeLength:0.0} mm, rapid: {Statistics.RapidLength:0.0} mm\n"));
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Estimated time: {Statistics.EstimatedMinutes:0.0} min, retracts: {Statistics.RetractCount}\n"));
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Levels: {Plan.Levels}, lowest level: {Plan.LowestLevel:0.000} mm"));
            return text.ToString();
        }
    }

    public void ShowResult(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Statistics = result.Statistics;
        Plan = result.Plan;
        RaiseStatistics();
    }

    public void Clear()
    {
        Statistics = null;
        Plan = null;
        RaiseStatistics();
    }

    protected override void OnReload()
    {
        OnPropertyChanged(nameof(Strategy));
        OnPropertyChanged(nameof(PostProcessor));
        OnPropertyChanged(nameof(CutScope));
        OnPropertyChanged(nameof(IsSeparation));
        OnPropertyChanged(nameof(MinIslandVolume));
        OnPropertyChanged(nameof(MinIslandVolumeError));
    }

    protected override void OnErrorsChanged()
    {
        base.OnErrorsChanged();
        OnPropertyChanged(nameof(MinIslandVolumeError));
    }

    private void RaiseStatistics()
    {
        OnPropertyChanged(nameof(Statistics));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(StatisticsText));
    }
}
