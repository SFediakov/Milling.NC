using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds ToolDefinition and draws a schematic: the head above the cutter, the cutter length below it.
public sealed class ToolSettingsViewModel : SettingsViewModelBase
{
    public const double SchematicWidth = 120;
    public const double SchematicHeight = 160;
    public const double SchematicHeadHeight = 36;
    private const double MinimumForScale = 1e-3;

    private static readonly string[] SchematicProperties =
    {
        nameof(HeadWidth), nameof(HeadLeft), nameof(CutterWidth), nameof(CutterLeft), nameof(CutterHeight), nameof(BallTop), nameof(IsBall),
    };

    public ToolSettingsViewModel(ProjectService project)
        : base(project, "Tool.")
    {
        Revalidate();
    }

    public static IReadOnlyList<TipType> TipTypes { get; } = Enum.GetValues<TipType>();

    public string Name
    {
        get => Current.Tool.Name;
        set => Edit(p => p.Tool.Name = value);
    }

    public float CutterDiameter
    {
        get => Current.Tool.CutterDiameter;
        set
        {
            Edit(p => p.Tool.CutterDiameter = value);
            RaiseSchematic();
        }
    }

    public float CutterLength
    {
        get => Current.Tool.CutterLength;
        set
        {
            Edit(p => p.Tool.CutterLength = value);
            RaiseSchematic();
        }
    }

    public float HeadDiameter
    {
        get => Current.Tool.HeadDiameter;
        set
        {
            Edit(p => p.Tool.HeadDiameter = value);
            RaiseSchematic();
        }
    }

    public TipType TipType
    {
        get => Current.Tool.TipType;
        set
        {
            Edit(p => p.Tool.TipType = value);
            RaiseSchematic();
        }
    }

    public string? CutterDiameterError => ErrorFor("Tool.CutterDiameter");

    public string? CutterLengthError => ErrorFor("Tool.CutterLength");

    public string? HeadDiameterError => ErrorFor("Tool.HeadDiameter");

    // Scale so that the wider of head and cutter fills the width and the cutter length fills the height.
    public double SchematicScale => Math.Min(
        SchematicWidth / Math.Max(Math.Max(HeadDiameter, CutterDiameter), MinimumForScale),
        (SchematicHeight - SchematicHeadHeight) / Math.Max(CutterLength, MinimumForScale));

    public double HeadWidth => HeadDiameter * SchematicScale;

    public double HeadLeft => (SchematicWidth - HeadWidth) / 2;

    public double CutterWidth => CutterDiameter * SchematicScale;

    public double CutterLeft => (SchematicWidth - CutterWidth) / 2;

    public double CutterHeight => CutterLength * SchematicScale;

    public double BallTop => SchematicHeadHeight + CutterHeight - CutterWidth / 2;

    public bool IsBall => TipType == TipType.Ball;

    protected override void OnReload()
    {
        foreach (var name in new[] { nameof(Name), nameof(CutterDiameter), nameof(CutterLength), nameof(HeadDiameter), nameof(TipType) })
        {
            OnPropertyChanged(name);
        }

        RaiseSchematic();
    }

    protected override void OnErrorsChanged()
    {
        base.OnErrorsChanged();
        OnPropertyChanged(nameof(CutterDiameterError));
        OnPropertyChanged(nameof(CutterLengthError));
        OnPropertyChanged(nameof(HeadDiameterError));
    }

    private void RaiseSchematic()
    {
        foreach (var name in SchematicProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
