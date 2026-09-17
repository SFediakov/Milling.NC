using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.App.Services;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// The Presets tab: the stored presets by name, a name box, and Save (the current Tool, Axes,
// Cutting and Strategy settings under that name), Load (into the project, as one edit so every
// panel reloads and the project is dirty) and Delete. File errors go to the error dialog.
public sealed partial class PresetsViewModel : SettingsViewModelBase
{
    public const string FieldPrefixPresets = "Presets.";

    private readonly PresetService _presets;
    private readonly IErrorDialogService _errors;

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private string _name = string.Empty;

    public PresetsViewModel(ProjectService project, PresetService presets, IErrorDialogService errors)
        : base(project, FieldPrefixPresets)
    {
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    public IReadOnlyList<string> Names => _presets.Presets.Select(p => p.Name).ToList();

    public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < _presets.Presets.Count;

    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var preset = MillingPreset.FromProject(Current, Name);
        try
        {
            _presets.Save(preset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _errors.ShowAsync(ex);
            return;
        }

        RefreshNames();
        SelectedIndex = IndexOf(preset.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Load()
    {
        var preset = _presets.Presets[SelectedIndex];
        Edit(p => preset.ApplyTo(p), null);
        Name = preset.Name;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var name = _presets.Presets[SelectedIndex].Name;
        try
        {
            _presets.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _errors.ShowAsync(ex);
            return;
        }

        SelectedIndex = -1;
        RefreshNames();
    }

    // Reads the file again (a missing file yields no presets; a corrupt one is reported).
    public async Task ReloadAsync()
    {
        try
        {
            _presets.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await _errors.ShowAsync(ex);
        }

        SelectedIndex = -1;
        RefreshNames();
    }

    protected override void OnReload()
    {
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (HasSelection)
        {
            Name = _presets.Presets[value].Name;
        }

        OnPropertyChanged(nameof(HasSelection));
        LoadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private int IndexOf(string name)
    {
        for (var k = 0; k < _presets.Presets.Count; k++)
        {
            if (string.Equals(_presets.Presets[k].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        return -1;
    }

    private void RefreshNames()
    {
        OnPropertyChanged(nameof(Names));
        OnPropertyChanged(nameof(HasSelection));
        LoadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }
}
