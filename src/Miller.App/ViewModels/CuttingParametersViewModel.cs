using System.Globalization;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds CuttingParameters and shows the grid the current stock and cell size would produce.
public sealed class CuttingParametersViewModel : SettingsViewModelBase
{
    private static readonly string[] BoundProperties =
    {
        nameof(FeedRate), nameof(PlungeRate), nameof(RapidRate), nameof(SpindleRpm), nameof(Stepover), nameof(FinishingStepover),
        nameof(Stepdown), nameof(SafeHeight), nameof(CellSize), nameof(Tolerance), nameof(Direction), nameof(GridSizeText),
    };

    private static readonly string[] ErrorProperties =
    {
        nameof(FeedRateError), nameof(PlungeRateError), nameof(RapidRateError), nameof(SpindleRpmError), nameof(StepoverError),
        nameof(FinishingStepoverError), nameof(StepdownError), nameof(SafeHeightError), nameof(CellSizeError), nameof(CellSizeWarning),
    };

    public CuttingParametersViewModel(ProjectService project)
        : base(project, "Parameters.")
    {
        Revalidate();
    }

    public static IReadOnlyList<MillingDirection> Directions { get; } = Enum.GetValues<MillingDirection>();

    public float FeedRate { get => Current.Parameters.FeedRate; set => Edit(p => p.Parameters.FeedRate = value); }

    public float PlungeRate { get => Current.Parameters.PlungeRate; set => Edit(p => p.Parameters.PlungeRate = value); }

    public float RapidRate { get => Current.Parameters.RapidRate; set => Edit(p => p.Parameters.RapidRate = value); }

    public float SpindleRpm { get => Current.Parameters.SpindleRpm; set => Edit(p => p.Parameters.SpindleRpm = value); }

    public float Stepover { get => Current.Parameters.Stepover; set => Edit(p => p.Parameters.Stepover = value); }

    public float FinishingStepover { get => Current.Parameters.FinishingStepover; set => Edit(p => p.Parameters.FinishingStepover = value); }

    public float Stepdown { get => Current.Parameters.Stepdown; set => Edit(p => p.Parameters.Stepdown = value); }

    public float SafeHeight { get => Current.Parameters.SafeHeight; set => Edit(p => p.Parameters.SafeHeight = value); }

    public float CellSize
    {
        get => Current.Parameters.CellSize;
        set
        {
            Edit(p => p.Parameters.CellSize = value);
            OnPropertyChanged(nameof(GridSizeText));
        }
    }

    public float Tolerance { get => Current.Parameters.Tolerance; set => Edit(p => p.Parameters.Tolerance = value); }

    public MillingDirection Direction { get => Current.Parameters.Direction; set => Edit(p => p.Parameters.Direction = value); }

    public string? FeedRateError => ErrorFor("Parameters.FeedRate");

    public string? PlungeRateError => ErrorFor("Parameters.PlungeRate");

    public string? RapidRateError => ErrorFor("Parameters.RapidRate");

    public string? SpindleRpmError => ErrorFor("Parameters.SpindleRpm");

    public string? StepoverError => ErrorFor("Parameters.Stepover");

    public string? FinishingStepoverError => ErrorFor("Parameters.FinishingStepover");

    public string? StepdownError => ErrorFor("Parameters.Stepdown");

    public string? SafeHeightError => ErrorFor("Parameters.SafeHeight");

    public string? CellSizeError => ErrorFor("Parameters.CellSize");

    public string? CellSizeWarning => WarningFor("Parameters.CellSize");

    // Cells of the stock bounding rectangle at the current cell size, against the validator limit.
    public string GridSizeText
    {
        get
        {
            var cell = Current.Parameters.CellSize;
            if (!(cell > 0))
            {
                return "Cell size must be positive.";
            }

            var size = AxisSetup.StockBoundingSize(Current.Stock);
            var width = (long)MathF.Ceiling(size.X / cell);
            var height = (long)MathF.Ceiling(size.Y / cell);
            return string.Create(CultureInfo.InvariantCulture, $"{width} x {height} = {width * height:N0} cells (limit {ProjectValidator.MaxCells:N0})");
        }
    }

    protected override void OnReload()
    {
        foreach (var name in BoundProperties)
        {
            OnPropertyChanged(name);
        }
    }

    protected override void OnErrorsChanged()
    {
        base.OnErrorsChanged();
        foreach (var name in ErrorProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
