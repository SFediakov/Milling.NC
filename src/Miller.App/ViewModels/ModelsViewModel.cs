using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// The models on the table: list, add and remove, and the placement (offset, rotation about Z) of the
// selected one, including centering it in the stock on one axis at a time. Selection is shared with
// the viewport through SelectedIndex.
public sealed partial class ModelsViewModel : SettingsViewModelBase
{
    public const string FieldPrefixModels = "Models";
    public const string NoSelectionText = "Select a model to place it.";

    private static readonly string[] PlacementProperties =
    {
        nameof(HasSelection), nameof(OffsetX), nameof(OffsetY), nameof(OffsetZ), nameof(RotationZ), nameof(SelectedName), nameof(PlacementText),
    };

    private readonly MeshImportService _meshImport;

    [ObservableProperty]
    private int _selectedIndex = -1;

    public ModelsViewModel(ProjectService project, MeshImportService meshImport, IAsyncRelayCommand addCommand)
        : base(project, FieldPrefixModels)
    {
        _meshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        AddCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        _meshImport.MeshChanged += (_, _) => OnReload();
    }

    public event EventHandler? SelectionChanged;

    public IAsyncRelayCommand AddCommand { get; }

    public IReadOnlyList<string> Names => Current.Models.Select(m => m.DisplayName).ToList();

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
        Edit(p => p.Models.RemoveAt(index), nameof(Names));
        _meshImport.RemoveAt(index);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterX() => Center(0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterY() => Center(1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CenterZ() => Center(2);

    protected override void OnReload()
    {
        OnPropertyChanged(nameof(Names));
        OnPropertyChanged(nameof(Count));
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

    private void Center(int axis)
    {
        var index = SelectedIndex;
        var offset = ModelLayout.CenteredOffset(Current, _meshImport.Bounds, index, axis);
        Edit(p => p.Models[index].Offset = offset, nameof(OffsetX));
        RaisePlacement();
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
    }
}
