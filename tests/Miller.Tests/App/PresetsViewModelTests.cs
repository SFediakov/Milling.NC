using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// The Presets tab: save the current settings under a name, load them back into the project as one
// dirty edit that reaches every panel, delete, enable rules, and the tab itself in the window.
public sealed class PresetsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-presets-vm-{Guid.NewGuid():N}");
    private readonly FakeErrorDialogService _errors = new();
    private readonly MainWindowViewModel _vm;

    public PresetsViewModelTests()
    {
        _vm = TestServices.MainWindowViewModel(_root, errors: _errors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task Save_StoresTheCurrentSettings_AndLoad_BringsThemBackAsADirtyEdit()
    {
        var presets = _vm.Presets;
        Assert.False(presets.SaveCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.False(presets.DeleteCommand.CanExecute(null));

        _vm.Tool.CutterDiameter = 3f;
        _vm.Cutting.FeedRate = 1500f;
        _vm.Axes.RotationZ = 45f;
        _vm.Strategy.ReachPercent = 70f;
        _vm.Strategy.CutScope = CutScope.Separation;
        presets.Name = " brass ";
        Assert.True(presets.SaveCommand.CanExecute(null));
        await presets.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "brass" }, presets.Names);
        Assert.Equal(0, presets.SelectedIndex);
        Assert.True(File.Exists(_vm.PresetStore.FilePath));
        Assert.Empty(_errors.Shown);

        _vm.NewProjectCommand.Execute(null);
        Assert.Equal(6f, _vm.Tool.CutterDiameter);
        Assert.False(_vm.Project.IsDirty);
        var raised = new List<string?>();
        _vm.Tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        presets.SelectedIndex = 0;
        Assert.True(presets.LoadCommand.CanExecute(null));
        presets.LoadCommand.Execute(null);
        Assert.Equal(3f, _vm.Tool.CutterDiameter);
        Assert.Equal(1500f, _vm.Cutting.FeedRate);
        Assert.Equal(45f, _vm.Axes.RotationZ);
        Assert.Equal(70f, _vm.Strategy.ReachPercent);
        Assert.Equal(CutScope.Separation, _vm.Strategy.CutScope);
        Assert.True(_vm.Project.IsDirty);
        Assert.Contains(nameof(ToolSettingsViewModel.CutterDiameter), raised);
        Assert.Equal("brass", presets.Name);

        // The project got copies: a later edit does not change the stored preset.
        _vm.Tool.CutterDiameter = 8f;
        Assert.Equal(3f, _vm.PresetStore.Presets[0].Tool.CutterDiameter);
    }

    [Fact]
    public async Task Delete_RemovesTheSelectedPreset_AndReload_ReadsTheFile()
    {
        var presets = _vm.Presets;
        presets.Name = "one";
        await presets.SaveCommand.ExecuteAsync(null);
        presets.Name = "two";
        await presets.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "one", "two" }, presets.Names);
        Assert.Equal(1, presets.SelectedIndex);

        await presets.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "one" }, presets.Names);
        Assert.Equal(-1, presets.SelectedIndex);
        Assert.False(presets.DeleteCommand.CanExecute(null));

        var other = TestServices.MainWindowViewModel(_root, errors: _errors);
        Assert.Empty(other.Presets.Names);
        await other.Presets.ReloadAsync();
        Assert.Equal(new[] { "one" }, other.Presets.Names);
        Assert.Empty(_errors.Shown);

        File.WriteAllText(other.PresetStore.FilePath, "{ broken");
        await other.Presets.ReloadAsync();
        Assert.Single(_errors.Shown);
        Assert.Empty(other.Presets.Names);
    }

    [AvaloniaFact]
    public void PresetsTab_IsTheFirstTab_AndShowsTheControls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-presets-tab-{Guid.NewGuid():N}");
        var window = new MainWindow { DataContext = TestServices.MainWindowViewModel(root) };
        try
        {
            window.Show();
            var tabs = window.GetLogicalDescendants().OfType<TabItem>().Select(t => t.Name).ToList();
            Assert.Equal("PresetsTab", tabs[0]);
            Assert.Contains("ToolTab", tabs);
            var view = window.GetLogicalDescendants().OfType<PresetsView>().Distinct().Single();
            Assert.NotNull(view.FindControl<ListBox>("PresetList"));
            Assert.NotNull(view.FindControl<TextBox>("PresetNameBox"));
            Assert.NotNull(view.FindControl<Button>("SavePresetButton"));
            Assert.NotNull(view.FindControl<Button>("LoadPresetButton"));
            Assert.NotNull(view.FindControl<Button>("DeletePresetButton"));
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
