using System.Diagnostics;
using System.Numerics;
using Miller.App.ViewModels;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-143: the machine tab connects only with a port or host, shows state and position, starts a
// program only after a confirmation and only from Idle, disables everything that moves the machine
// while a job runs, shows progress, failure lines and a lost connection, keeps the console, saves its
// fields, and moves the viewport marker to the machine with the answered part of the toolpath drawn
// as done.
public sealed class MachineViewModelTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-machine-vm-{Guid.NewGuid():N}");
    private readonly List<FakeGrblLink> _links = new();
    private readonly FakeConfirmDialogService _confirm = new();
    private readonly ViewportViewModel _viewport = new();
    private readonly Toolpath _toolpath = Square();
    private readonly SettingsService _settings;
    private readonly MachineService _service;
    private readonly MachineViewModel _vm;
    private bool _simulationPlaying;

    public MachineViewModelTests()
    {
        _settings = new SettingsService(Path.Combine(_root, "settings"));
        _service = new MachineService(_ =>
        {
            _links.Add(new FakeGrblLink());
            return _links[^1];
        }, Fast);
        _vm = new MachineViewModel(_service, _settings, _viewport, new FakeFileDialogService(), _confirm, () => _toolpath, () => new MillingProject(),
            () => _simulationPlaying, TestServices.Version);
    }

    public void Dispose()
    {
        _vm.Close();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private FakeGrblLink Link => _links[^1];

    private static Toolpath Square()
    {
        var toolpath = new Toolpath();
        var corners = new[] { new Vector3(0, 0, 1), new Vector3(10, 0, 1), new Vector3(10, 10, 1), new Vector3(0, 10, 1) };
        toolpath.Add(new ToolpathSegment(new Vector3(0, 0, 5), corners[0], MoveKind.Plunge, 200));
        for (var k = 1; k < corners.Length; k++)
        {
            toolpath.Add(new ToolpathSegment(corners[k - 1], corners[k], MoveKind.Feed, 800));
        }

        return toolpath;
    }

    // The condition may read the controller directly, which can be one step ahead of the view model,
    // so the view model is refreshed once more after it holds.
    private async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            _vm.Refresh();
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
        _vm.ConnectionIndex = 0;
        _vm.SerialPort = "fake";
        await _vm.ConnectCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.StateText == GrblStatus.Idle);
    }

    [Fact]
    public async Task Connect_NeedsAPort_ThenShowsTheMachine()
    {
        _vm.SerialPort = string.Empty;
        _vm.ConnectionIndex = 0;
        await _vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(MachineViewModel.ChoosePortMessage, _vm.Message);
        Assert.Empty(_links);
        _vm.ConnectionIndex = 1;
        Assert.True(_vm.IsNetwork);
        _vm.Host = " ";
        await _vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(MachineViewModel.EnterHostMessage, _vm.Message);
        _vm.Host = "cnc.local";
        _vm.NetworkPort = 70000;
        await _vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(MachineViewModel.NetworkPortMessage, _vm.Message);
        Assert.Empty(_links);

        await ConnectAsync();
        Assert.Null(_vm.Message);
        Assert.Equal("fake, Grbl 1.1h", _vm.ConnectionText);
        Assert.Equal(("0.500", "1.500", "2.500"), (_vm.WorkX, _vm.WorkY, _vm.WorkZ));
        Assert.Equal(("1.000", "2.000", "3.000"), (_vm.MachineX, _vm.MachineY, _vm.MachineZ));
        Assert.Equal("100%", _vm.FeedOverrideText);
        Assert.False(_vm.ConnectCommand.CanExecute(null));
        Assert.True(_vm.DisconnectCommand.CanExecute(null));
        Assert.True(_vm.HomeCommand.CanExecute(null));
        Assert.False(_vm.StartCommand.CanExecute(null));
        Assert.True(_viewport.MachineLive);
        Assert.Equal(new Vector3(0.5f, 1.5f, 2.5f), _viewport.ToolPosition);
        Assert.Equal("fake", _settings.Machine.SerialPort);
        Assert.Equal(MachineConnectionKind.Serial, _settings.Machine.Connection);

        await _vm.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(MachineViewModel.DisconnectedText, _vm.StateText);
        Assert.False(_viewport.MachineLive);
    }

    [Fact]
    public async Task Start_AsksFirst_ThenShowsProgressAlongTheToolpath()
    {
        await ConnectAsync();
        _viewport.SetToolpath(_toolpath);
        _vm.UseToolpathCommand.Execute(null);
        Assert.Equal($"{MachineService.GeneratedProgramName}, 10 lines", _vm.ProgramText);
        Assert.True(_vm.StartCommand.CanExecute(null));
        string? status = null;
        _vm.StatusChanged += (_, s) => status = s;

        await _vm.StartCommand.ExecuteAsync(null);
        Assert.Single(_confirm.Questions);
        Assert.Contains("10 lines", _confirm.Questions[0], StringComparison.Ordinal);
        Assert.Empty(Link.Lines);

        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _service.Snapshot!.Job is { State: JobState.Done });
        Assert.Equal(1f, _vm.Progress);
        Assert.Equal("11 of 11 lines", _vm.ProgressText);
        Assert.StartsWith("Program done", _vm.JobText, StringComparison.Ordinal);
        Assert.False(_vm.JobFailed);
        Assert.Equal(_toolpath.Count, _viewport.ToolpathProgressIndex);
        Assert.Contains("done", status, StringComparison.Ordinal);
        Assert.Equal(1, Link.MaxInFlight);
    }

    [Fact]
    public async Task WhileAJobRuns_NothingThatMovesTheMachineIsEnabled()
    {
        await ConnectAsync();
        Link.OkDelay = TimeSpan.FromMilliseconds(30);
        _vm.UseToolpathCommand.Execute(null);
        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.IsJobActive);
        foreach (var command in new[] { _vm.StartCommand, _vm.CheckCommand, _vm.OutlineCommand, _vm.HomeCommand, _vm.UnlockCommand, _vm.GoToZeroCommand, _vm.ProbeCommand, _vm.SendCommand, _vm.UseToolpathCommand, _vm.OpenFileCommand, _vm.ConnectCommand })
        {
            Assert.False(command.CanExecute(null));
        }

        Assert.False(_vm.JogCommand.CanExecute("X+"));
        Assert.False(_vm.ZeroCommand.CanExecute("XYZ"));
        Assert.True(_vm.PauseCommand.CanExecute(null));
        Assert.True(_vm.StopCommand.CanExecute(null));
        Assert.True(_vm.OverrideCommand.CanExecute("F+"));

        _vm.StopCommand.Execute(null);
        await UntilAsync(() => _service.Snapshot!.Job is { State: JobState.Stopped } && _vm.HomeCommand.CanExecute(null));
        Assert.Contains("stopped", _vm.JobText, StringComparison.Ordinal);
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, Link.RealtimeBytes);
    }

    [Fact]
    public async Task Jog_UsesTheStepAndFeed_AndABadFeedSendsNothing()
    {
        await ConnectAsync();
        _vm.JogStep = 0.1f;
        _vm.JogFeed = 250;
        _vm.JogCommand.Execute("X-");
        await UntilAsync(() => Link.Lines.Count == 1 && !_service.Snapshot!.LineInFlight);
        Assert.Equal("$J=G91G21X-0.1F250", Link.Lines[0]);
        Assert.Equal(0.1f, _settings.Machine.JogStep);

        _vm.JogFeed = 0;
        _vm.JogCommand.Execute("Z+");
        Assert.Contains("feed", _vm.Message, StringComparison.OrdinalIgnoreCase);
        _vm.ZeroCommand.Execute("XYZ");
        await UntilAsync(() => Link.Lines.Count == 2 && !_service.Snapshot!.LineInFlight);
        Assert.Equal("G10L20P0X0Y0Z0", Link.Lines[1]);
        _vm.OverrideCommand.Execute("S-");
        await UntilAsync(() => Link.RealtimeBytes.Count == 1);
        Assert.Equal(GrblRealtime.SpindleMinus10, Link.RealtimeBytes[0]);
    }

    [Fact]
    public async Task AFailedLine_IsShownWithItsNumberAndMeaning()
    {
        await ConnectAsync();
        Link.Respond = line => line.StartsWith("G1X10Y0", StringComparison.Ordinal) ? "error:33" : "ok";
        _vm.UseToolpathCommand.Execute(null);
        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.JobFailed);
        Assert.Contains("Line", _vm.JobText, StringComparison.Ordinal);
        Assert.Contains("Motion command target is invalid", _vm.JobText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_SendsALine_AndShowsTheTraffic()
    {
        await ConnectAsync();
        _vm.ConsoleInput = "$$";
        _vm.SendCommand.Execute(null);
        await UntilAsync(() => _vm.ConsoleLines.Contains("in   ok"));
        Assert.Contains("out  $$", _vm.ConsoleLines);
        Assert.Equal(string.Empty, _vm.ConsoleInput);
        Assert.Contains(_vm.ConsoleLines, l => l.Contains("Connected to fake", StringComparison.Ordinal));

        _vm.ConsoleInput = "G1 X1 !";
        _vm.SendCommand.Execute(null);
        Assert.Contains("realtime", _vm.Message, StringComparison.Ordinal);
        Assert.Equal("G1 X1 !", _vm.ConsoleInput);
    }

    [Fact]
    public async Task ALostLink_IsShownWithItsReason()
    {
        await ConnectAsync();
        Link.Fail();
        await UntilAsync(() => _vm.StateText == MachineViewModel.DisconnectedText);
        Assert.Contains("removed", _vm.Message, StringComparison.Ordinal);
        Assert.False(_viewport.MachineLive);
        Assert.True(_vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_DuringAJob_AsksFirst()
    {
        await ConnectAsync();
        Link.OkDelay = TimeSpan.FromMilliseconds(30);
        _vm.UseToolpathCommand.Execute(null);
        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.IsJobActive);
        await _vm.DisconnectCommand.ExecuteAsync(null);
        Assert.True(_vm.IsConnected);
        _confirm.Confirmations.Enqueue(true);
        await _vm.DisconnectCommand.ExecuteAsync(null);
        Assert.False(_vm.IsConnected);
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, Link.RealtimeBytes);
    }

    [Fact]
    public async Task PlayingSimulation_KeepsTheViewportMarker()
    {
        await ConnectAsync();
        _viewport.SetToolProgress(2, new Vector3(7, 7, 7));
        _simulationPlaying = true;
        _vm.Refresh();
        Assert.Equal(new Vector3(7, 7, 7), _viewport.ToolPosition);
        _simulationPlaying = false;
        _vm.Refresh();
        Assert.Equal(new Vector3(0.5f, 1.5f, 2.5f), _viewport.ToolPosition);
        Assert.Equal(2, _viewport.ToolpathProgressIndex);
    }
}
