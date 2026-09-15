using System.Globalization;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds AxisSetup and shows where the model lands in machine space for the current setup.
public sealed class AxisSettingsViewModel : SettingsViewModelBase
{
    public const string NoModelText = "No model loaded.";

    private static readonly string[] BoundProperties =
    {
        nameof(MapX), nameof(MapY), nameof(MapZ), nameof(FlipX), nameof(FlipY), nameof(FlipZ), nameof(RotationX), nameof(RotationY), nameof(RotationZ),
        nameof(OriginMode), nameof(OffsetX), nameof(OffsetY), nameof(OffsetZ), nameof(IsCustom), nameof(MachineBoundsText),
    };

    private readonly MeshImportService _meshImport;

    public AxisSettingsViewModel(ProjectService project, MeshImportService meshImport)
        : base(project, "Axes.")
    {
        _meshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        _meshImport.MeshChanged += (_, _) => OnPropertyChanged(nameof(MachineBoundsText));
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
        }
    }

    public float OffsetX { get => Current.Axes.CustomOffset.X; set => Set(p => p.Axes.CustomOffset = new Vector3(value, p.Axes.CustomOffset.Y, p.Axes.CustomOffset.Z)); }

    public float OffsetY { get => Current.Axes.CustomOffset.Y; set => Set(p => p.Axes.CustomOffset = new Vector3(p.Axes.CustomOffset.X, value, p.Axes.CustomOffset.Z)); }

    public float OffsetZ { get => Current.Axes.CustomOffset.Z; set => Set(p => p.Axes.CustomOffset = new Vector3(p.Axes.CustomOffset.X, p.Axes.CustomOffset.Y, value)); }

    public bool IsCustom => OriginMode == OriginMode.Custom;

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

            var bounds = ModelLayout.MachineBounds(Current, _meshImport.Bounds);
            var size = bounds.Size;
            return string.Create(CultureInfo.InvariantCulture,
                $"{size.X:0.000} x {size.Y:0.000} x {size.Z:0.000} mm, min ({bounds.Min.X:0.000}, {bounds.Min.Y:0.000}, {bounds.Min.Z:0.000})");
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
    }

    private void Set(Action<MillingProject> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        Edit(change, property);
        OnPropertyChanged(nameof(MachineBoundsText));
    }
}
