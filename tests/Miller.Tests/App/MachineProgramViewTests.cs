using System.Diagnostics;
using System.Numerics;
using Miller.App.ViewModels;
using Miller.Application.Services;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-198 to T-200 in the machine tab: the open tab switches the viewport to the program's path with the
// cutter at the machine (0, 0, 0 while unknown) and leaving it brings the generated toolpath back
// unchanged; the work coordinate fields go out as G10 L20; the listing marks the run row by row; Save
// progress asks for a new file and writes the resume program.
public sealed class MachineProgramViewTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-machine-view-{Guid.NewGuid():N}");
    private readonly List<FakeGrblLink> _links = new();
    private readonly FakeConfirmDialogService _confirm = new();
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ViewportViewModel _viewport = new();
    private readonly Toolpath _toolpath = Square();
    private readonly MachineService _service;
    private readonly MachineViewModel _vm;

    public MachineProgramViewTests()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        _service = new MachineService(_ =>
        {
            _links.Add(new FakeGrblLink());
            return _links[^1];
        }, Fast);
        _vm = new MachineViewModel(_service, settings, _viewport, _dialogs, _confirm, () => _toolpath, () => new MillingProject(),
            () => false, TestServices.Version);
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
        _vm.SerialPort = "fake";
        await _vm.ConnectCommand.ExecuteAsync(null);
        await UntilAsync(() => _vm.StateText == GrblStatus.Idle);
    }

    private async Task RunToolpathAsync()
    {
        _vm.UseToolpathCommand.Execute(null);
        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _service.RunJob is { State: JobState.Done });
    }

    [Fact]
    public void Viewport_MachineView_HasItsOwnPathProgressAndCutter_AndLeavingRestoresTheToolpath()
    {
        _viewport.SetToolpath(_toolpath);
        _viewport.SetToolProgress(2, new Vector3(1, 2, 3));
        _viewport.SetMeshes(new[] { new Mesh(new[] { new Triangle(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)) }) });
        _viewport.ClearFitRequest();
        var version = _viewport.ToolpathVersion;
        var path = new[] { new ToolpathSegment(new Vector3(-5, -5, 2), new Vector3(15, 25, -3), MoveKind.Feed, 100) };

        _viewport.ShowMachineView(path, 1, new Vector3(7, 8, 9));
        Assert.True(_viewport.MachineView);
        Assert.Same(path, _viewport.DisplayedSegments);
        Assert.Equal(1, _viewport.DisplayedProgressIndex);
        Assert.Equal(new Vector3(7, 8, 9), _viewport.DisplayedToolPosition);
        Assert.False(_viewport.DisplayModel);
        Assert.False(_viewport.DisplayStock);
        Assert.True(_viewport.DisplayTool);
        Assert.True(_viewport.ToolpathVersion > version);
        Assert.True(_viewport.FitPending);
        Assert.Equal(new BoundingBox(new Vector3(-5, -5, -3), new Vector3(15, 25, 2)), _viewport.FitBounds);
        Assert.False(_viewport.BeginDrag(new Vector3(0.2f, 0.2f, 10), -Vector3.UnitZ));

        _viewport.HideMachineView();
        Assert.False(_viewport.MachineView);
        Assert.Same(_toolpath.Segments, _viewport.DisplayedSegments);
        Assert.Equal(2, _viewport.DisplayedProgressIndex);
        Assert.Equal(new Vector3(1, 2, 3), _viewport.DisplayedToolPosition);
        Assert.True(_viewport.DisplayModel);

        // The same path again keeps the camera; a new one is framed.
        _viewport.ClearFitRequest();
        _viewport.ShowMachineView(path, 0, Vector3.Zero);
        Assert.False(_viewport.FitPending);
        _viewport.ShowMachineView(path.ToArray(), 0, Vector3.Zero);
        Assert.True(_viewport.FitPending);
    }

    [Fact]
    public async Task OpenTab_ShowsTheProgramPath_WithTheCutterAtZeroUntilTheMachineIsKnown()
    {
        _viewport.SetToolpath(_toolpath);
        _vm.IsViewActive = true;
        Assert.True(_viewport.MachineView);
        Assert.Empty(_viewport.DisplayedSegments);
        Assert.Equal(Vector3.Zero, _viewport.DisplayedToolPosition);
        Assert.True(_viewport.DisplayTool);

        _vm.UseToolpathCommand.Execute(null);
        _vm.Refresh();
        Assert.Same(_service.Program!.Path.Segments, _viewport.DisplayedSegments);
        Assert.Equal(_toolpath.Count + 2, _viewport.DisplayedSegments.Count);
        Assert.Equal(Vector3.Zero, _viewport.DisplayedToolPosition);

        await ConnectAsync();
        Assert.Equal(new Vector3(0.5f, 1.5f, 2.5f), _viewport.DisplayedToolPosition);
        await RunToolpathAsync();
        Assert.Equal(_viewport.DisplayedSegments.Count, _viewport.DisplayedProgressIndex);

        _vm.IsViewActive = false;
        Assert.False(_viewport.MachineView);
        Assert.Same(_toolpath.Segments, _viewport.DisplayedSegments);
    }

    [Fact]
    public async Task WorkCoordinates_GoOutForTheNamedAxes_AndABadValueSendsNothing()
    {
        Assert.False(_vm.SetWorkCommand.CanExecute("X"));
        Assert.False(_vm.ReadWorkCommand.CanExecute(null));
        await ConnectAsync();
        _vm.ReadWorkCommand.Execute(null);
        Assert.Equal((0.5f, 1.5f, 2.5f), (_vm.WorkValueX, _vm.WorkValueY, _vm.WorkValueZ));

        _vm.WorkValueX = 12.5f;
        _vm.WorkValueY = -3f;
        _vm.WorkValueZ = 0.25f;
        _vm.SetWorkCommand.Execute("Y");
        await UntilAsync(() => Link.Lines.Count == 1 && !_service.Snapshot!.LineInFlight && _vm.SetWorkCommand.CanExecute("X"));
        _vm.SetWorkCommand.Execute("XYZ");
        await UntilAsync(() => Link.Lines.Count == 2 && !_service.Snapshot!.LineInFlight && _vm.SetWorkCommand.CanExecute("X"));
        Assert.Equal(new[] { "G10L20P0Y-3", "G10L20P0X12.5Y-3Z0.25" }, Link.Lines);

        _vm.WorkValueZ = float.PositiveInfinity;
        _vm.SetWorkCommand.Execute("Z");
        Assert.NotNull(_vm.Message);
        Assert.Equal(2, Link.Lines.Count);
    }

    [Fact]
    public async Task Listing_FollowsTheRun_AndNamesTheExecutingRow()
    {
        Assert.Equal(ProgramListingViewModel.NoProgramText, _vm.Listing.Description);
        _vm.UseToolpathCommand.Execute(null);
        _vm.Refresh();
        var listing = _vm.Listing;
        Assert.Equal(_service.Program!.Grbl.Text.Count(c => c == '\n'), listing.Rows.Count);
        Assert.Equal(ProgramListingViewModel.NotStartedText, listing.Description);
        Assert.All(listing.Rows, r => Assert.Equal(ProgramRowState.Pending, r.State));
        Assert.Equal(1, listing.Rows[0].Number);

        await ConnectAsync();
        await RunToolpathAsync();
        var last = listing.Rows.Count - 1;
        Assert.Equal(last, listing.CurrentRow);
        Assert.StartsWith($"Finished at line {last + 1} of {listing.Rows.Count}: M30", listing.Description, StringComparison.Ordinal);
        Assert.True(listing.Rows[last].IsExecuting);
        Assert.All(listing.Rows.Take(last), r => Assert.True(r.IsExecuted));
    }

    [Fact]
    public async Task SaveProgress_AsksForANewFile_AndWritesTheResumeProgram()
    {
        Assert.False(_vm.SaveProgressCommand.CanExecute(null));
        await ConnectAsync();
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "part.nc");
        File.WriteAllText(source, "G21 G90\nG0 Z5\nG0 X0 Y0\n" + string.Concat(Enumerable.Range(1, 40).Select(k => $"G1 X{k} Y0 Z-1 F900\n")) + "M30\n");
        _dialogs.OpenResults.Enqueue(source);
        await _vm.OpenFileCommand.ExecuteAsync(null);
        Link.OkDelay = TimeSpan.FromMilliseconds(5);
        _confirm.Confirmations.Enqueue(true);
        await _vm.StartCommand.ExecuteAsync(null);
        await UntilAsync(() => _service.RunJob is { Acknowledged: >= 12 });
        Assert.False(_vm.SaveProgressCommand.CanExecute(null));
        _vm.StopCommand.Execute(null);
        await UntilAsync(() => _service.RunJob is { State: JobState.Stopped } && _vm.SaveProgressCommand.CanExecute(null));

        string? status = null;
        _vm.StatusChanged += (_, s) => status = s;
        var output = Path.Combine(_root, "part_resume.nc");
        _dialogs.SaveResults.Enqueue(output);
        await _vm.SaveProgressCommand.ExecuteAsync(null);
        Assert.Equal(MachineViewModel.SaveProgressTitle, _dialogs.Titles[^1]);
        Assert.Equal(GrblResume.Build(_service.RunProgram!.Grbl, _service.RunJob!.Acknowledged), File.ReadAllText(output));
        Assert.Contains(output, status, StringComparison.Ordinal);
        Assert.Null(_vm.Message);

        _dialogs.SaveResults.Enqueue(source);
        await _vm.SaveProgressCommand.ExecuteAsync(null);
        Assert.Equal(MachineService.SameFileMessage, _vm.Message);
    }
}
