using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.Geometry;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Binds StockDefinition. Fit to model sizes the stock around the oriented model plus the margin in
// X and Y; the height equals the model height because the model top sits at the stock top.
public sealed partial class StockSettingsViewModel : SettingsViewModelBase
{
    private static readonly string[] BoundProperties =
    {
        nameof(Shape), nameof(SizeX), nameof(SizeY), nameof(SizeZ), nameof(Diameter), nameof(Height), nameof(Placement), nameof(Margin),
        nameof(OriginX), nameof(OriginY), nameof(OriginZ), nameof(IsBox), nameof(IsCylinder), nameof(IsExplicit),
    };

    private readonly MeshImportService _meshImport;

    public StockSettingsViewModel(ProjectService project, MeshImportService meshImport)
        : base(project, "Stock.")
    {
        _meshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        _meshImport.MeshChanged += (_, _) =>
        {
            FitToModelCommand.NotifyCanExecuteChanged();
            Revalidate();
        };
        Revalidate();
    }

    public static IReadOnlyList<StockShape> Shapes { get; } = Enum.GetValues<StockShape>();

    public static IReadOnlyList<StockPlacement> Placements { get; } = Enum.GetValues<StockPlacement>();

    public StockShape Shape
    {
        get => Current.Stock.Shape;
        set
        {
            Edit(p => p.Stock.Shape = value);
            OnPropertyChanged(nameof(IsBox));
            OnPropertyChanged(nameof(IsCylinder));
        }
    }

    public float SizeX { get => Current.Stock.SizeX; set => Edit(p => p.Stock.SizeX = value); }

    public float SizeY { get => Current.Stock.SizeY; set => Edit(p => p.Stock.SizeY = value); }

    public float SizeZ { get => Current.Stock.SizeZ; set => Edit(p => p.Stock.SizeZ = value); }

    public float Diameter { get => Current.Stock.Diameter; set => Edit(p => p.Stock.Diameter = value); }

    public float Height { get => Current.Stock.Height; set => Edit(p => p.Stock.Height = value); }

    public StockPlacement Placement
    {
        get => Current.Stock.Placement;
        set
        {
            Edit(p => p.Stock.Placement = value);
            OnPropertyChanged(nameof(IsExplicit));
        }
    }

    public float Margin { get => Current.Stock.Margin; set => Edit(p => p.Stock.Margin = value); }

    public float OriginX { get => Current.Stock.ExplicitOrigin.X; set => Edit(p => p.Stock.ExplicitOrigin = new Vector3(value, p.Stock.ExplicitOrigin.Y, p.Stock.ExplicitOrigin.Z)); }

    public float OriginY { get => Current.Stock.ExplicitOrigin.Y; set => Edit(p => p.Stock.ExplicitOrigin = new Vector3(p.Stock.ExplicitOrigin.X, value, p.Stock.ExplicitOrigin.Z)); }

    public float OriginZ { get => Current.Stock.ExplicitOrigin.Z; set => Edit(p => p.Stock.ExplicitOrigin = new Vector3(p.Stock.ExplicitOrigin.X, p.Stock.ExplicitOrigin.Y, value)); }

    public bool IsBox => Shape == StockShape.Box;

    public bool IsCylinder => Shape == StockShape.Cylinder;

    public bool IsExplicit => Placement == StockPlacement.Explicit;

    public string? SizeXError => ErrorFor("Stock.SizeX");

    public string? SizeYError => ErrorFor("Stock.SizeY");

    public string? SizeZError => ErrorFor("Stock.SizeZ");

    public string? DiameterError => ErrorFor("Stock.Diameter");

    public string? HeightError => ErrorFor("Stock.Height");

    public string? MarginError => ErrorFor("Stock.Margin");

    public string? PlacementWarning => WarningFor("Stock.Placement");

    public bool CanFitToModel => _meshImport.HasMesh && Current.Axes.IsPermutation;

    [RelayCommand(CanExecute = nameof(CanFitToModel))]
    private void FitToModel()
    {
        var size = OrientedModelBounds().Size;
        var margin = Margin;
        Edit(p =>
        {
            if (p.Stock.Shape == StockShape.Box)
            {
                p.Stock.SizeX = size.X + 2 * margin;
                p.Stock.SizeY = size.Y + 2 * margin;
                p.Stock.SizeZ = size.Z;
            }
            else
            {
                p.Stock.Diameter = MathF.Sqrt(size.X * size.X + size.Y * size.Y) + 2 * margin;
                p.Stock.Height = size.Z;
            }
        });
        OnReload();
    }

    protected override BoundingBox? ModelBoundsForValidation()
    {
        if (!_meshImport.HasMesh || !Current.Axes.IsPermutation)
        {
            return null;
        }

        var mesh = _meshImport.CurrentMesh!;
        return AxisSetup.TransformBounds(mesh.Bounds, Current.Axes.ToMatrix(mesh.Bounds, Current.Stock));
    }

    protected override void OnReload()
    {
        foreach (var name in BoundProperties)
        {
            OnPropertyChanged(name);
        }

        FitToModelCommand.NotifyCanExecuteChanged();
    }

    protected override void OnErrorsChanged()
    {
        base.OnErrorsChanged();
        foreach (var name in new[] { nameof(SizeXError), nameof(SizeYError), nameof(SizeZError), nameof(DiameterError), nameof(HeightError), nameof(MarginError), nameof(PlacementWarning) })
        {
            OnPropertyChanged(name);
        }
    }

    private BoundingBox OrientedModelBounds()
        => AxisSetup.TransformBounds(_meshImport.CurrentMesh!.Bounds, Current.Axes.ToOrientationMatrix());
}
