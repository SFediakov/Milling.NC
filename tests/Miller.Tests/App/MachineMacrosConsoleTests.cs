using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Miller.App.Rendering;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Application.Macros;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-201, T-202 and T-204 in the window: the next moves are a View option of their own; the console
// sends while a program is paused and not while it runs; the macro editor stores, renames and deletes
// macros, a macro button sends its lines, and a corrupt macro file leaves the tab working.
public sealed class MachineMacrosConsoleTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-macros-vm-{Guid.NewGuid():N}");
    private readonly List<FakeGrblLink> _links = new();
    private readonly FakeConfirmDialogService _confirm = new();
    private readonly ViewportViewModel _viewport = new();
    private MachineService? _service;
    private MachineViewModel? _vm;

    public void Dispose()
    {
        _vm?.Close();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private FakeGrblLink Link => _links[^1];

    private MachineViewModel Create()
    {
        _service = new MachineService(_ =>
        {
            _links.Add(new FakeGrblLink());
            return _links[^1];
        }, Fast);
        _vm = new MachineViewModel(_service, new SettingsService(Path.Combine(_root, "settings")), _viewport, new FakeFileDialogService(), _confirm,
            () => null, () => new MillingProject(), () => false, TestServices.Version);
        return _vm;
    }

    private async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _vm!.Refresh();
            if (condition())
            {
                _vm.Refresh();
                return;
            }

            Assert.True(watch.ElapsedMilliseconds < 10_000, "condition not reached in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    private async Task ConnectAsync()
    {
        _vm!.SerialPort = "fake";
        await _vm.ConnectCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.StateText == GrblStatus.Idle);
    }

    [Fact]
    public void NextMoves_AreTheTwentySegmentsAfterTheProgress_AndNothingAtTheEnd()
    {
        Assert.Equal(20, ViewportViewModel.NextMoveCount);
        Assert.Equal((0, 40), ToolpathRenderer.NextRange(0, 100, 20));
        Assert.Equal((60, 40), ToolpathRenderer.NextRange(30, 100, 20));
        Assert.Equal((180, 20), ToolpathRenderer.NextRange(90, 100, 20));
        Assert.Equal((200, 0), ToolpathRenderer.NextRange(100, 100, 20));
        Assert.Equal((200, 0), ToolpathRenderer.NextRange(150, 100, 20));
        Assert.Equal((0, 0), ToolpathRenderer.NextRange(-5, 0, 20));

        var segments = new[] { new ToolpathSegment(new Vector3(1, 2, 3), new Vector3(4, 5, 6), MoveKind.Feed, 100) };
        var color = new Vector4(0.1f, 0.2f, 0.3f, 1);
        Assert.Equal(new[] { 1f, 2, 3, 0.1f, 0.2f, 0.3f, 1, 4, 5, 6, 0.1f, 0.2f, 0.3f, 1 }, ToolpathRenderer.BuildHighlight(segments, color));

        var viewport = new ViewportViewModel();
        var renders = 0;
        viewport.RequestRender += (_, _) => renders++;
        Assert.False(viewport.ShowNextMoves);
        viewport.ShowNextMoves = true;
        Assert.Equal(1, renders);
    }

    [AvaloniaFact]
    public void ViewMenu_HasFullToolpathAndNextMovesAsSeparateOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-nextmoves-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            var items = window.GetLogicalDescendants().OfType<MenuItem>().Distinct().ToDictionary(m => m.Name ?? string.Empty);
            Assert.Equal("Show _Full Toolpath", items["ShowToolpathItem"].Header);
            var next = items["ShowNextMovesItem"];
            Assert.Equal(MenuItemToggleType.CheckBox, next.ToggleType);
            Assert.False(next.IsChecked);
            next.IsChecked = true;
            Assert.True(viewModel.Viewport.ShowNextMoves);
            Assert.True(viewModel.Viewport.ShowToolpath);

            window.GetLogicalDescendants().OfType<TabItem>().First().Focus();
            window.KeyPressQwerty(PhysicalKey.Digit5, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Digit5, RawInputModifiers.Control);
            Assert.False(viewModel.Viewport.ShowNextMoves);
            Assert.False(next.IsChecked);
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

    [Fact]
    public async Task Console_SendsIntoAPausedProgram_ButNotIntoARunningOne()
    {
        var vm = Create();
        await ConnectAsync();
        Directory.CreateDirectory(_root);
        Link.OkDelay = TimeSpan.FromMilliseconds(10);
        var source = Path.Combine(_root, "long.nc");
        File.WriteAllText(source, "G0 Z5\n" + string.Concat(Enumerable.Range(1, 200).Select(k => $"G1 X{k} Z-1 F900\n")));
        _service!.LoadFile(source);
        _confirm.Confirmations.Enqueue(true);
        await vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _service.Snapshot!.Job is { Acknowledged: >= 3 });
        Assert.False(vm.SendCommand.CanExecute(null));

        vm.PauseCommand.Execute(null);
        await UntilAsync(() => vm.StateText.StartsWith(GrblStatus.Hold, StringComparison.Ordinal) && vm.SendCommand.CanExecute(null));
        vm.ConsoleInput = "G0 Z12";
        vm.SendCommand.Execute(null);
        Assert.Null(vm.Message);
        Assert.Equal(string.Empty, vm.ConsoleInput);
        await UntilAsync(() => Link.Lines.Contains("G0Z12"));
        Assert.Contains(vm.ConsoleLines, l => l == "out  G0Z12");

        vm.ResumeCommand.Execute(null);
        await UntilAsync(() => _service.Snapshot!.Job is { State: JobState.Done });
        Assert.Equal(202, _service.RunJob!.Acknowledged);
    }

    [Fact]
    public async Task MacroEditor_SavesRenamesAndDeletes_AndAButtonSendsTheLines()
    {
        var vm = Create();
        Assert.Empty(vm.Macros);
        Assert.False(vm.DeleteMacroCommand.CanExecute(null));

        vm.MacroName = "Lift";
        vm.MacroDelay = 50;
        vm.MacroText = "G91 G0 Z5\nG90";
        vm.SaveMacroCommand.Execute(null);
        Assert.Null(vm.Message);
        Assert.Equal(new[] { "Lift" }, vm.MacroNames);
        Assert.Equal(0, vm.MacroIndex);

        vm.NewMacroCommand.Execute(null);
        Assert.Equal(-1, vm.MacroIndex);
        Assert.Equal(string.Empty, vm.MacroText);
        vm.MacroName = "lift";
        vm.MacroText = "M8";
        vm.SaveMacroCommand.Execute(null);
        Assert.Contains("exists already", vm.Message, StringComparison.Ordinal);
        vm.MacroName = "Air";
        vm.MacroDelay = 12.5f;
        vm.SaveMacroCommand.Execute(null);
        Assert.Equal(MachineViewModel.MacroDelayMessage, vm.Message);
        vm.MacroDelay = 0;
        vm.SaveMacroCommand.Execute(null);
        Assert.Equal(new[] { "Air", "Lift" }, vm.MacroNames);
        Assert.Equal(0, vm.MacroIndex);

        // Selecting loads the editor; saving under a new name renames.
        vm.MacroIndex = 1;
        Assert.Equal(("Lift", 50f, "G91 G0 Z5\nG90"), (vm.MacroName, vm.MacroDelay, vm.MacroText));
        vm.MacroName = "Lift 5";
        vm.SaveMacroCommand.Execute(null);
        Assert.Equal(new[] { "Air", "Lift 5" }, vm.MacroNames);
        Assert.Equal(1, vm.MacroIndex);

        Assert.False(vm.RunMacroCommand.CanExecute(vm.Macros[1]));
        await ConnectAsync();
        Assert.True(vm.RunMacroCommand.CanExecute(vm.Macros[1]));
        vm.RunMacroCommand.Execute(vm.Macros[1]);
        await UntilAsync(() => _service!.Snapshot!.Job is { IsActive: false } && Link.Lines.Count == 2);
        Assert.Equal(new[] { "G91G0Z5", "G90" }, Link.Lines);

        _confirm.Confirmations.Enqueue(false);
        await vm.DeleteMacroCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Macros.Count);
        _confirm.Confirmations.Enqueue(true);
        await vm.DeleteMacroCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Air" }, vm.MacroNames);
        Assert.Equal(-1, vm.MacroIndex);

        // A new view model reads the same file.
        _vm!.Close();
        var again = Create();
        Assert.Equal(new[] { "Air" }, again.MacroNames);
    }

    [Fact]
    public async Task CorruptMacroFile_IsReported_AndTheTabWorksOn()
    {
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(Path.Combine(_root, "settings", MacroService.FileName), "{ broken");
        var vm = Create();
        Assert.StartsWith("Macros not loaded:", vm.Message, StringComparison.Ordinal);
        Assert.Empty(vm.Macros);
        await ConnectAsync();
        Assert.True(vm.HomeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void MacrosSection_ShowsOneButtonPerMacro_BoundToRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-macro-ui-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.Machine.MacroName = "Park";
            viewModel.Machine.MacroText = "G53 G0 Z-1";
            viewModel.Machine.SaveMacroCommand.Execute(null);
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("MachineTab");
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetLogicalDescendants().OfType<MachineView>().Distinct().Single();
            var buttons = panel.FindControl<ItemsControl>("MacroButtons")!.GetVisualDescendants().OfType<Button>().ToList();
            var button = Assert.Single(buttons);
            Assert.Equal("Park", button.Content);
            Assert.Same(viewModel.Machine.RunMacroCommand, button.Command);
            Assert.Same(viewModel.Machine.Macros[0], button.CommandParameter);
            Assert.Equal(new[] { "Park" }, panel.FindControl<ComboBox>("MacroBox")!.ItemsSource!.Cast<string>());
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
