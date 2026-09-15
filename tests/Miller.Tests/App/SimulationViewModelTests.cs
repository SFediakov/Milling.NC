using Miller.App.Converters;
using Miller.App.ViewModels;
using Miller.Core.Simulation;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class SimulationViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-simvm-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<MainWindowViewModel> LoadedAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        var stl = Path.Combine(_root, "box.stl");
        File.WriteAllText(stl, TestMeshes.AsciiCubeText());
        _dialogs.OpenResults.Enqueue(stl);
        await vm.OpenStlCommand.ExecuteAsync(null);
        vm.Stock.SizeX = 10;
        vm.Stock.SizeY = 10;
        vm.Stock.SizeZ = 3;
        vm.Cutting.CellSize = 0.5f;
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(vm.SimulationPanel.IsLoaded);
        return vm;
    }

    [Theory]
    [InlineData(0.0, 0.1f)]
    [InlineData(0.25, 1f)]
    [InlineData(0.5, 10f)]
    [InlineData(1.0, 1000f)]
    [InlineData(-1.0, 0.1f)]
    [InlineData(2.0, 1000f)]
    public void Converter_MapsSliderToSpeed(double slider, float speed)
    {
        Assert.Equal(speed, LogSliderConverter.ToSpeed(slider), 3);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.37f)]
    [InlineData(1f)]
    [InlineData(42f)]
    [InlineData(1000f)]
    public void Converter_RoundTripsWithinTolerance(float speed)
    {
        var converter = new LogSliderConverter();
        var slider = Assert.IsType<double>(converter.Convert(speed, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
        var back = Assert.IsType<float>(converter.ConvertBack(slider, typeof(float), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(MathF.Abs(back - speed) <= 1e-4f * MathF.Max(1f, speed), $"{speed} -> {slider} -> {back}");
        Assert.Equal(LogSliderConverter.ToSlider(5000f), LogSliderConverter.ToSlider(1000f));
    }

    [Fact]
    public void WithoutAToolpath_EveryCommandIsDisabled()
    {
        Directory.CreateDirectory(_root);
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        var panel = vm.SimulationPanel;
        Assert.False(panel.IsLoaded);
        Assert.Equal(SimulationViewModel.NotLoadedText, panel.StateText);
        Assert.False(panel.PlayCommand.CanExecute(null));
        Assert.False(panel.PauseCommand.CanExecute(null));
        Assert.False(panel.StopCommand.CanExecute(null));
        Assert.False(panel.StepCommand.CanExecute(null));
        Assert.False(panel.RunToEndCommand.CanExecute(null));
    }

    [Fact]
    public async Task Loaded_Playing_Paused_Finished_EnableTheRightCommands()
    {
        var vm = await LoadedAsync();
        var panel = vm.SimulationPanel;
        Assert.Equal(SimulationViewModel.ReadyStatus, panel.StateText);
        Assert.True(panel.PlayCommand.CanExecute(null));
        Assert.False(panel.PauseCommand.CanExecute(null));
        Assert.True(panel.StopCommand.CanExecute(null));
        Assert.True(panel.StepCommand.CanExecute(null));
        Assert.True(panel.RunToEndCommand.CanExecute(null));

        panel.PlayCommand.Execute(null);
        Assert.True(panel.IsPlaying);
        Assert.Equal(SimulationViewModel.PlayingStatus, vm.StatusText);
        Assert.False(panel.PlayCommand.CanExecute(null));
        Assert.True(panel.PauseCommand.CanExecute(null));
        Assert.False(panel.StepCommand.CanExecute(null));
        Assert.False(panel.RunToEndCommand.CanExecute(null));

        panel.TogglePlayPause();
        Assert.False(panel.IsPlaying);
        Assert.Equal(SimulationViewModel.PausedStatus, vm.StatusText);
        Assert.True(panel.PlayCommand.CanExecute(null));
        panel.TogglePlayPause();
        Assert.True(panel.IsPlaying);
        panel.PauseCommand.Execute(null);

        await panel.RunToEndCommand.ExecuteAsync(null);
        Assert.True(panel.IsFinished);
        Assert.False(panel.IsRunningToEnd);
        Assert.Equal(1f, panel.Progress);
        Assert.StartsWith(SimulationViewModel.FinishedStatus, vm.StatusText);
        Assert.Equal(SimulationViewModel.FinishedStatus, panel.StateText);
        Assert.False(panel.PlayCommand.CanExecute(null));
        Assert.False(panel.StepCommand.CanExecute(null));
        Assert.True(panel.StopCommand.CanExecute(null));
        Assert.Equal(vm.LastResult!.Toolpath.Count, vm.Viewport.ToolpathProgressIndex);
        Assert.True(vm.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Step_AdvancesOneSimulatedSecond_AndStop_ResetsProgress()
    {
        var vm = await LoadedAsync();
        var panel = vm.SimulationPanel;
        panel.StepCommand.Execute(null);
        Assert.True(panel.Progress > 0f);
        Assert.Equal(SimulationViewModel.FormatSeconds(0), panel.ElapsedText);
        Assert.True(vm.Viewport.ToolpathProgressIndex >= 0);
        Assert.Equal(SimulationViewModel.PausedStatus, panel.StateText);

        panel.StopCommand.Execute(null);
        Assert.Equal(0f, panel.Progress);
        Assert.Equal(0, panel.CollisionCount);
        Assert.Equal(SimulationViewModel.StoppedStatus, vm.StatusText);
        Assert.Equal(SimulationViewModel.ReadyStatus, panel.StateText);
        Assert.Equal(0, vm.Viewport.ToolpathProgressIndex);
        Assert.Same(vm.Simulation.Stock, vm.Viewport.StockMap);
    }

    [Fact]
    public async Task SpeedText_ClampsAndReportsInvalidInput_AndPersistsTheSetting()
    {
        var vm = await LoadedAsync();
        var panel = vm.SimulationPanel;
        panel.SpeedText = "5000";
        Assert.Equal(SimulationClock.MaxSpeedFactor, panel.SpeedFactor);
        Assert.Equal("1000", panel.SpeedText);
        Assert.Contains("clamped", panel.SpeedError);
        Assert.Equal(1000f, vm.Settings.SpeedFactor);

        panel.SpeedText = "abc";
        Assert.Equal(SimulationViewModel.SpeedInvalidMessage, panel.SpeedError);
        Assert.Equal(1000f, panel.SpeedFactor);

        panel.SpeedText = "2.5";
        Assert.Null(panel.SpeedError);
        Assert.Equal(2.5f, panel.SpeedFactor);
        Assert.Equal(2.5f, vm.Settings.SpeedFactor);

        var reopened = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        reopened.Settings.Load();
        Assert.Equal(2.5f, reopened.Settings.SpeedFactor);
    }

    [Fact]
    public void FormatSeconds_ShowsMinutesAndSeconds()
    {
        Assert.Equal("0:00.0 min", SimulationViewModel.FormatSeconds(0));
        Assert.Equal("2:05.5 min", SimulationViewModel.FormatSeconds(125.5));
    }
}
