using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.GCode;
using Miller.Core.Generation;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.App.ViewModels;

// Routing strategy and post-processor choice from the registries, the cut scope, the reach rule,
// the collision mode with its ratio (X for recursion, Y for one run), the generate and cancel
// commands of the main view model, and the statistics of the last run.
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

    public static IReadOnlyList<CollisionMode> CollisionModes { get; } = Enum.GetValues<CollisionMode>();

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

    public float ReachPercent { get => Current.ReachPercent; set => Edit(p => p.ReachPercent = value); }

    public string? ReachPercentError => ErrorFor("Strategy.ReachPercent");

    public CollisionMode CollisionMode
    {
        get => Current.CollisionMode;
        set
        {
            Edit(p => p.CollisionMode = value);
            OnPropertyChanged(nameof(IsRecursion));
            OnPropertyChanged(nameof(IsOneRun));
        }
    }

    public bool IsRecursion => Current.CollisionMode == CollisionMode.Recursion;

    public bool IsOneRun => Current.CollisionMode == CollisionMode.OneRun;

    public float RecursionRatio { get => Current.RecursionRatio; set => Edit(p => p.RecursionRatio = value); }

    public string? RecursionRatioError => ErrorFor("Strategy.RecursionRatio");

    public float OneRunRatio { get => Current.OneRunRatio; set => Edit(p => p.OneRunRatio = value); }

    public string? OneRunRatioError => ErrorFor("Strategy.OneRunRatio");

    public ToolpathStatistics? Statistics { get; private set; }

    public SlicePlan? Plan { get; private set; }

    public int Passes { get; private set; }

    public IReadOnlyList<PassCollisions> PassCollisions { get; private set; } = Array.Empty<PassCollisions>();

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
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Levels: {Plan.Levels}, lowest level: {Plan.LowestLevel:0.000} mm\n"));
            text.Append(string.Create(CultureInfo.InvariantCulture, $"Generation passes: {Passes}, collisions per pass: {string.Join(", ", PassCollisions.Select(p => p.Events.ToString(CultureInfo.InvariantCulture)))}"));
            return text.ToString();
        }
    }

    public void ShowResult(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Statistics = result.Statistics;
        Plan = result.Plan;
        Passes = result.Passes;
        PassCollisions = result.PassCollisions;
        RaiseStatistics();
    }

    public void Clear()
    {
        Statistics = null;
        Plan = null;
        Passes = 0;
        PassCollisions = Array.Empty<PassCollisions>();
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
        OnPropertyChanged(nameof(ReachPercent));
        OnPropertyChanged(nameof(ReachPercentError));
        OnPropertyChanged(nameof(CollisionMode));
        OnPropertyChanged(nameof(IsRecursion));
        OnPropertyChanged(nameof(IsOneRun));
        OnPropertyChanged(nameof(RecursionRatio));
        OnPropertyChanged(nameof(RecursionRatioError));
        OnPropertyChanged(nameof(OneRunRatio));
        OnPropertyChanged(nameof(OneRunRatioError));
    }

    protected override void OnErrorsChanged()
    {
        base.OnErrorsChanged();
        OnPropertyChanged(nameof(MinIslandVolumeError));
        OnPropertyChanged(nameof(ReachPercentError));
        OnPropertyChanged(nameof(RecursionRatioError));
        OnPropertyChanged(nameof(OneRunRatioError));
    }

    private void RaiseStatistics()
    {
        OnPropertyChanged(nameof(Statistics));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(StatisticsText));
    }
}
