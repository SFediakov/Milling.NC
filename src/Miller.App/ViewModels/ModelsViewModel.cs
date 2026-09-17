using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Model extreme to bring to the stock extreme on one axis.
public enum AlignTarget
{
    XMin,
    XCenter,
    XMax,
    YMin,
    YCenter,
    YMax,
    ZMin,
    ZCenter,
    ZMax,
}

// The models on the table: list, add and remove, the auto-fit alignment of the stock around all
// models per axis, and the placement (offset, rotation about Z) of the selected one, including
// aligning it to the stock minimum, middle or maximum on one axis at a time and moving it by a
// viewport drag. Selection is shared with the viewport through SelectedIndex.
public sealed partial class ModelsViewModel : SettingsViewModelBase
{
    public const string FieldPrefixModels = "Models";
    public const string NoSelectionText = "Select a model to place it.";

    private static readonly string[] PlacementProperties =
    {
        nameof(HasSelection), nameof(OffsetX), nameof(OffsetY), nameof(OffsetZ), nameof(RotationZ), nameof(SelectedName), nameof(PlacementText),
    };

    private static readonly string[] StockProperties =
    {
        nameof(StockAlignX), nameof(StockAlignY), nameof(StockAlignZ), nameof(IsAutoFit),
    };

    private readonly MeshImportService _meshImport;

    // Published to the list box only when a name is added, removed or renamed: a new list instance
    // on every project edit made the list box reset its selection, which disabled the placement
    // fields and took the keyboard focus away in the middle of typing a value.
    private IReadOnlyList<string> _names = Array.Empty<string>();

    [ObservableProperty]
    private int _selectedIndex = -1;

    public ModelsViewModel(ProjectService project, MeshImportService meshImport, IAsyncRelayCommand addCommand)
        : base(project, FieldPrefixModels)
    {
        _meshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        AddCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        _meshImport.MeshChanged += (_, _) => OnReload();
        RefreshNames();
    }

    public event EventHandler? SelectionChanged;

    public IAsyncRelayCommand AddCommand { get; }

    public static IReadOnlyList<StockAlignment> StockAlignments { get; } = Enum.GetValues<StockAlignment>();

    public bool IsAutoFit => Current.Stock.Placement == StockPlacement.AutoFitWithMargin;

    public StockAlignment StockAlignX { get => Current.Stock.AlignX; set => Edit(p => p.Stock.AlignX = value); }

    public StockAlignment StockAlignY { get => Current.Stock.AlignY; set => Edit(p => p.Stock.AlignY = value); }

    public StockAlignment StockAlignZ { get => Current.Stock.AlignZ; set => Edit(p => p.Stock.AlignZ = value); }

    public IReadOnlyList<string> Names => _names;

    public int Count => Current.Models.Count;

    public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < Current.Models.Count && SelectedIndex < _meshImport.Meshes.Count;

    public string SelectedName => HasSelection ? Current.Models[SelectedIndex].DisplayName : string.Empty;

    public string PlacementText => HasSelection ? $"Placement of {SelectedName} (mm, degrees)" : NoSelectionText;

    public float OffsetX
    {
        get => HasSelection ? Current.Models[SelectedIndex].Offset.X : 0f;
        set => EditOffset(o => new Vector3(value, o.Y, o.Z));
    }

    public float OffsetY
    {
        get => HasSelection ? Current.Models[SelectedIndex].Offset.Y : 0f;
        set => EditOffset(o => new Vector3(o.X, value, o.Z));
    }

    public float OffsetZ
    {
        get => HasSelection ? Current.Models[SelectedIndex].Offset.Z : 0f;
        set => EditOffset(o => new Vector3(o.X, o.Y, value));
    }

    public float RotationZ
    {
        get => HasSelection ? Current.Models[SelectedIndex].RotationZ : 0f;
        set
        {
            if (HasSelection)
            {
                var index = SelectedIndex;
                Edit(p => p.Models[index].RotationZ = value);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var index = SelectedIndex;
        SelectedIndex = -1;
        Edit(p => p.Models.RemoveAt(index), null);
        _meshImport.RemoveAt(index);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterX() => Align(AlignTarget.XCenter);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterY() => Align(AlignTarget.YCenter);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterZ() => Align(AlignTarget.ZCenter);

    // Moves the selected model by its offset to the stock minimum, middle or maximum on one axis.
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Align(AlignTarget target)
    {
        var index = SelectedIndex;
        var axis = (int)target / 3;
        var alignment = (StockAlignment)((int)target % 3);
        var offset = ModelLayout.AlignedOffset(Current, _meshImport.Bounds, index, axis, alignment);
        Edit(p => p.Models[index].Offset = offset, nameof(OffsetX));
        RaisePlacement();
    }

    // A viewport drag: the selected model moves by the machine-space XY delta, which equals the
    // offset delta because machine zero does not follow the offsets.
    public void MoveSelected(Vector2 delta)
    {
        if (!HasSelection || delta == Vector2.Zero)
        {
            return;
        }

        EditOffset(o => new Vector3(o.X + delta.X, o.Y + delta.Y, o.Z), nameof(OffsetX));
        OnPropertyChanged(nameof(OffsetY));
    }

    protected override void OnReload()
    {
        RefreshNames();
        OnPropertyChanged(nameof(Count));
        foreach (var name in StockProperties)
        {
            OnPropertyChanged(name);
        }

        if (SelectedIndex >= Current.Models.Count)
        {
            SelectedIndex = -1;
        }

        RaisePlacement();
    }

    partial void OnSelectedIndexChanged(int value)
    {
        RaisePlacement();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditOffset(Func<Vector3, Vector3> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (!HasSelection)
        {
            return;
        }

        var index = SelectedIndex;
        Edit(p => p.Models[index].Offset = change(p.Models[index].Offset), property);
    }

    private void RefreshNames()
    {
        var names = Current.Models.Select(m => m.DisplayName).ToList();
        if (names.SequenceEqual(_names, StringComparer.Ordinal))
        {
            return;
        }

        _names = names;
        OnPropertyChanged(nameof(Names));
    }

    private void RaisePlacement()
    {
        foreach (var name in PlacementProperties)
        {
            OnPropertyChanged(name);
        }

        RemoveCommand.NotifyCanExecuteChanged();
        CenterXCommand.NotifyCanExecuteChanged();
        CenterYCommand.NotifyCanExecuteChanged();
        CenterZCommand.NotifyCanExecuteChanged();
        AlignCommand.NotifyCanExecuteChanged();
    }
}
