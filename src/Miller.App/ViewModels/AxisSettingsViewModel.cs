using System.Globalization;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Geometry;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds AxisSetup and shows where the model and the stock land in machine space for the current setup.
public sealed class AxisSettingsViewModel : SettingsViewModelBase
{
    public const string NoModelText = "No model loaded.";

    public const string StockOffsetLabel = "Custom zero relative to the stock corner (mm)";

    public const string ModelOffsetLabel = "Custom zero relative to the models' corner (mm)";

    public const string GlobalOffsetLabel = "Zero in the global frame, the model coordinates after the axis mapping (mm)";

    private static readonly string[] BoundProperties =
    {
        nameof(MapX), nameof(MapY), nameof(MapZ), nameof(FlipX), nameof(FlipY), nameof(FlipZ), nameof(RotationX), nameof(RotationY), nameof(RotationZ),
        nameof(OriginMode), nameof(OffsetX), nameof(OffsetY), nameof(OffsetZ), nameof(IsCustom), nameof(OffsetLabel), nameof(MachineBoundsText),
        nameof(StockBoundsText),
    };

    private readonly MeshImportService _meshImport;

    public AxisSettingsViewModel(ProjectService project, MeshImportService meshImport)
        : base(project, "Axes.")
    {
        _meshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        _meshImport.MeshChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MachineBoundsText));
            OnPropertyChanged(nameof(StockBoundsText));
        };
        Revalidate();
    }

    public static IReadOnlyList<ModelAxis> Axes { get; } = Enum.GetValues<ModelAxis>();

    public static IReadOnlyList<OriginMode> OriginModes { get; } = Enum.GetValues<OriginMode>();

    public ModelAxis MapX { get => Current.Axes.MapX; set => Set(p => p.Axes.MapX = value); }

    public ModelAxis MapY { get => Current.Axes.MapY; set => Set(p => p.Axes.MapY = value); }

    public ModelAxis MapZ { get => Current.Axes.MapZ; set => Set(p => p.Axes.MapZ = value); }

    public bool FlipX { get => Current.Axes.FlipX; set => Set(p => p.Axes.FlipX = value); }

    public bool FlipY { get => Current.Axes.FlipY; set => Set(p => p.Axes.FlipY = value); }

    public bool FlipZ { get => Current.Axes.FlipZ; set => Set(p => p.Axes.FlipZ = value); }

    public float RotationX { get => Current.Axes.RotationX; set => Set(p => p.Axes.RotationX = value); }

    public float RotationY { get => Current.Axes.RotationY; set => Set(p => p.Axes.RotationY = value); }

    public float RotationZ { get => Current.Axes.RotationZ; set => Set(p => p.Axes.RotationZ = value); }

    public OriginMode OriginMode
    {
        get => Current.Axes.OriginMode;
        set
        {
            Set(p => p.Axes.OriginMode = value);
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(OffsetLabel));
        }
    }

    public float OffsetX { get => Current.Axes.CustomOffset.X; set => Set(p => p.Axes.CustomOffset = new Vector3(value, p.Axes.CustomOffset.Y, p.Axes.CustomOffset.Z)); }

    public float OffsetY { get => Current.Axes.CustomOffset.Y; set => Set(p => p.Axes.CustomOffset = new Vector3(p.Axes.CustomOffset.X, value, p.Axes.CustomOffset.Z)); }

    public float OffsetZ { get => Current.Axes.CustomOffset.Z; set => Set(p => p.Axes.CustomOffset = new Vector3(p.Axes.CustomOffset.X, p.Axes.CustomOffset.Y, value)); }

    // The modes that place zero by CustomOffset, and what the offset is measured from.
    public bool IsCustom => OriginMode is OriginMode.Custom or OriginMode.ModelCustom or OriginMode.Global;

    public string OffsetLabel => OriginMode switch
    {
        OriginMode.ModelCustom => ModelOffsetLabel,
        OriginMode.Global => GlobalOffsetLabel,
        _ => StockOffsetLabel,
    };

    public string? MappingError => ErrorFor("Axes.MapX");

    // Size and minimum corner of the oriented, positioned model; the validator message when the
    // mapping is not a permutation.
    public string MachineBoundsText
    {
        get
        {
            if (!Current.Axes.IsPermutation)
            {
                return MappingError ?? string.Empty;
            }

            if (!_meshImport.Matches(Current))
            {
                return NoModelText;
            }

            return BoxText(ModelLayout.MachineBounds(Current, _meshImport.Bounds));
        }
    }

    // The stock box in machine space, the same box the viewport outlines and the pipeline cuts.
    public string StockBoundsText
    {
        get
        {
            if (!Current.Axes.IsPermutation)
            {
                return MappingError ?? string.Empty;
            }

            return _meshImport.Matches(Current) ? BoxText(ModelLayout.StockBoundsMachine(Current, _meshImport.Bounds)) : NoModelText;
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
        OnPropertyChanged(nameof(MappingError));
        OnPropertyChanged(nameof(MachineBoundsText));
        OnPropertyChanged(nameof(StockBoundsText));
    }

    private static string BoxText(BoundingBox bounds)
    {
        var size = bounds.Size;
        return string.Create(CultureInfo.InvariantCulture,
            $"{size.X:0.000} x {size.Y:0.000} x {size.Z:0.000} mm, min ({bounds.Min.X:0.000}, {bounds.Min.Y:0.000}, {bounds.Min.Z:0.000})");
    }

    private void Set(Action<MillingProject> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        Edit(change, property);
        OnPropertyChanged(nameof(MachineBoundsText));
        OnPropertyChanged(nameof(StockBoundsText));
    }
}
