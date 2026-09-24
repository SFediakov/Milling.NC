using Avalonia;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds ToolDefinition and draws a schematic: the head above the cutter (a rectangle for a cylinder,
// a trapezoid from the bottom to the top diameter for a frustum), the cutter length below it.
public sealed class ToolSettingsViewModel : SettingsViewModelBase
{
    public const double SchematicWidth = 120;
    public const double SchematicHeight = 160;
    public const double SchematicHeadHeight = 36;
    private const double MinimumForScale = 1e-3;

    private static readonly string[] SchematicProperties =
    {
        nameof(HeadWidth), nameof(HeadOutline), nameof(CutterWidth), nameof(CutterLeft), nameof(CutterHeight), nameof(BallTop), nameof(IsBall),
    };

    public ToolSettingsViewModel(ProjectService project)
        : base(project, "Tool.")
    {
        Revalidate();
    }

    public static IReadOnlyList<TipType> TipTypes { get; } = Enum.GetValues<TipType>();

    public static IReadOnlyList<HeadShape> HeadShapes { get; } = Enum.GetValues<HeadShape>();

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

    public HeadShape HeadShape
    {
        get => Current.Tool.HeadShape;
        set
        {
            Edit(p => p.Tool.HeadShape = value);
            OnPropertyChanged(nameof(IsFrustum));
            RaiseSchematic();
        }
    }

    public float HeadTopDiameter
    {
        get => Current.Tool.HeadTopDiameter;
        set
        {
            Edit(p => p.Tool.HeadTopDiameter = value);
            RaiseSchematic();
        }
    }

    public float HeadLength
    {
        get => Current.Tool.HeadLength;
        set => Edit(p => p.Tool.HeadLength = value);
    }

    public bool IsFrustum => HeadShape == HeadShape.Frustum;

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

    public string? HeadTopDiameterError => ErrorFor("Tool.HeadTopDiameter");

    public string? HeadLengthError => ErrorFor("Tool.HeadLength");

    // Scale so that the widest of head and cutter fills the width and the cutter length fills the height.
    public double SchematicScale => Math.Min(
        SchematicWidth / Math.Max(Math.Max(Current.Tool.HeadRadius * 2, CutterDiameter), MinimumForScale),
        (SchematicHeight - SchematicHeadHeight) / Math.Max(CutterLength, MinimumForScale));

    // The widest head width in the schematic.
    public double HeadWidth => Current.Tool.HeadRadius * 2 * SchematicScale;

    // Bottom left, bottom right, top right, top left; the head bottom sits on the cutter.
    public IList<Point> HeadOutline
    {
        get
        {
            var bottom = HeadDiameter * SchematicScale;
            var top = IsFrustum ? HeadTopDiameter * SchematicScale : bottom;
            return new List<Point>
            {
                new((SchematicWidth - bottom) / 2, SchematicHeadHeight),
                new((SchematicWidth + bottom) / 2, SchematicHeadHeight),
                new((SchematicWidth + top) / 2, 0),
                new((SchematicWidth - top) / 2, 0),
            };
        }
    }

    public double CutterWidth => CutterDiameter * SchematicScale;

    public double CutterLeft => (SchematicWidth - CutterWidth) / 2;

    public double CutterHeight => CutterLength * SchematicScale;

    public double BallTop => SchematicHeadHeight + CutterHeight - CutterWidth / 2;

    public bool IsBall => TipType == TipType.Ball;

    protected override void OnReload()
    {
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(CutterDiameter), nameof(CutterLength), nameof(HeadDiameter), nameof(HeadShape), nameof(HeadTopDiameter),
                     nameof(HeadLength), nameof(IsFrustum), nameof(TipType),
                 })
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
        OnPropertyChanged(nameof(HeadTopDiameterError));
        OnPropertyChanged(nameof(HeadLengthError));
    }

    private void RaiseSchematic()
    {
        foreach (var name in SchematicProperties)
        {
            OnPropertyChanged(name);
        }
    }
}
