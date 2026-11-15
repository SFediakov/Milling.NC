using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Miller.App.Rendering;
using Miller.App.ViewModels;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-219 (user request): the time left and Save progress count the lines the machine executed, not the
// lines it received. Grbl acknowledges a line once it is in its planner, ahead of the tool; the executed
// lines are those up to the last move the tool finished, found from its reported position (ExecutionTracker
// in the machine gate), never more than the acknowledged ones, and every line once the run is done.
public sealed partial class ExecutedLinesTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private static readonly MachineConnectionSettings Settings = new(MachineConnectionKind.Serial, "fake", SerialLink.DefaultBaudRate, string.Empty, TcpLink.DefaultPort);

    // 300 cuts along X at Z -1: the move to X k is line k + 1 and segment k (segment 0 is the rise to Z 5).
    private static readonly string Text = "G21 G90\nG0 Z5\n" + string.Concat(Enumerable.Range(1, 300).Select(k => $"G1 X{k} Y0 Z-1 F900\n")) + "M30\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-executed-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [GeneratedRegex(@"about (\d+):(\d\d) left")]
    private static partial Regex TimeLeft();

    // The fake's work offset is 0.5 on every axis.
    private static GrblPoint AtWork(double x) => new(x + 0.5, 0.5, -0.5);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < 10_000, "condition not reached in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task SaveProgress_ResumesFromTheMoveTheToolWasOn_NotFromTheAcknowledgedLines()
    {
        // The tool stands halfway along the move to X40 (segment 40, line 41) while Grbl acknowledged every
        // program line (the closing G4 P0 is held back, so the run goes on until it is stopped).
        var links = new List<FakeGrblLink>();
        using var service = new MachineService(_ =>
        {
            links.Add(new FakeGrblLink { MachinePosition = AtWork(39.5), Respond = line => line == MachineJob.SyncLine ? null : "ok" });
            return links[^1];
        }, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        var program = service.LoadFile(Write("long.nc", Text));
        Assert.Equal(41, program.Path.LineOf(40));
        service.StartProgram();
        await WaitForAsync(() => service.RunJob is { Acknowledged: >= 100 } && service.RunExecuting() == 40);

        // Lines 0 to 40 (up to the move to X39) are executed.
        var executed = program.Path.LineOf(39) + 1;
        Assert.Equal(41, executed);
        Assert.Equal(executed, service.RunExecutedLines());
        Assert.True(service.RunJob!.Acknowledged > executed);

        service.Stop();
        await WaitForAsync(() => service.Snapshot!.Job is { State: JobState.Stopped } && service.Snapshot.CanRun && service.Snapshot.Status.State == GrblStatus.Idle);
        var output = Path.Combine(_root, "long_resume.nc");
        service.SaveProgress(output);
        var resumed = File.ReadAllText(output);
        Assert.Equal(GrblResume.Build(program.Grbl, executed), resumed);
        var lines = resumed.Split('\n');
        Assert.Contains("G1 X40 Y0 Z-1 F900", lines);
        Assert.DoesNotContain(lines, l => l.Contains("X37", StringComparison.Ordinal));

        // A reconnection keeps what was executed.
        service.Disconnect();
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        Assert.Equal(executed, service.RunExecutedLines());
        service.SaveProgress(output);
        Assert.Equal(resumed, File.ReadAllText(output));
    }

    [Fact]
    public async Task TheExecutedLines_NeverPassTheAcknowledgedOnes_AndAreEveryLineOnceDone()
    {
        // The reported position lies far beyond what Grbl has received.
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(2), MachinePosition = AtWork(250) };
        using var service = new MachineService(_ => link, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        service.LoadFile(Write("long.nc", Text));
        service.StartProgram();
        await WaitForAsync(() => service.RunJob is { Acknowledged: >= 20 });
        for (var k = 0; k < 20 && service.RunJob!.IsActive; k++)
        {
            var job = service.RunJob!;
            Assert.InRange(service.RunExecutedLines(), 0, service.RunJob!.Acknowledged);
            Assert.True(service.RunExecutedLines() <= job.Total);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await WaitForAsync(() => service.RunJob is { State: JobState.Done });
        Assert.Equal(service.RunJob!.Total, service.RunExecutedLines());
    }

    // T-220 (user request): everything else follows the executed lines as well: the progress bar, the line
    // count with the file line of the move the tool is on, the part of the generated toolpath drawn as done
    // and the next moves highlighted after it, and the listing row.
    [Fact]
    public async Task TheBarTheLineCountAndTheViewport_FollowTheExecutedLines()
    {
        // A plunge to Z -1 at X0, then 40 cuts of 1 mm along X. Every program line is acknowledged (the closing
        // G4 P0 is held back) while the tool is halfway along the cut from X10 to X11: toolpath segment 11,
        // program path segment 13 (after the two lead-in moves to safe Z and to the start).
        var toolpath = new Toolpath();
        toolpath.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -1), MoveKind.Plunge, CuttingParameters.DefaultFeedRate));
        for (var k = 1; k <= 40; k++)
        {
            toolpath.Add(new ToolpathSegment(new Vector3(k - 1, 0, -1), new Vector3(k, 0, -1), MoveKind.Feed, CuttingParameters.DefaultFeedRate));
        }

        var link = new FakeGrblLink { MachinePosition = AtWork(10.5), Respond = line => line == MachineJob.SyncLine ? null : "ok" };
        using var service = new MachineService(_ => link, Fast);
        var viewport = new ViewportViewModel();
        viewport.SetToolpath(toolpath);
        var vm = new MachineViewModel(service, new SettingsService(Path.Combine(_root, "vm-settings")), viewport, new FakeFileDialogService(), new FakeConfirmDialogService(),
            () => toolpath, () => new MillingProject(), () => false, TestServices.Version);
        try
        {
            await service.ConnectAsync(Settings);
            await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
            vm.UseToolpathCommand.Execute(null);
            var program = service.Program!;
            service.StartProgram();
            var total = program.Grbl.Count + 1;
            await WaitForAsync(() => service.RunJob is { Acknowledged: var acknowledged } && acknowledged == total - 1 && service.RunExecuting() == 13);
            vm.Refresh();

            var executed = service.RunExecutedLines();
            Assert.Equal(program.Path.LineOf(12) + 1, executed);
            Assert.True(executed < service.RunJob!.Acknowledged);
            var source = program.Grbl.SourceLines[program.Path.LineOf(13)];
            Assert.Equal(source, service.RunExecutingSourceLine());
            Assert.Equal((float)executed / total, vm.Progress);
            Assert.Equal($"{executed} of {total} lines, file line {source}", vm.ProgressText);

            Assert.Equal(11, viewport.ToolpathProgressIndex);
            Assert.Equal((11 * 2, Math.Min(ViewportViewModel.NextMoveCount, toolpath.Count - 11) * 2),
                ToolpathRenderer.NextRange(viewport.ToolpathProgressIndex, toolpath.Count, ViewportViewModel.NextMoveCount));
            Assert.Contains($"line {source} of", vm.Listing.Description, StringComparison.Ordinal);

            vm.IsViewActive = true;
            Assert.Equal(13, viewport.DisplayedProgressIndex);
        }
        finally
        {
            vm.Close();
        }
    }

    [Fact]
    public async Task TheTimeLeft_ScalesWithTheExecutedLines()
    {
        // Grbl acknowledged every program line (the closing G4 P0 is held back, so the run goes on) while
        // the tool is on the move to X50: 51 of the 304 job lines executed, so about five times the elapsed
        // time is left; the acknowledged lines (303) would give almost nothing.
        var link = new FakeGrblLink { MachinePosition = AtWork(49.5), Respond = line => line == MachineJob.SyncLine ? null : "ok" };
        using var service = new MachineService(_ => link, Fast);
        var root = Path.Combine(_root, "vm");
        var vm = new MachineViewModel(service, new SettingsService(Path.Combine(root, "settings")), new ViewportViewModel(), new FakeFileDialogService(), new FakeConfirmDialogService(),
            () => null, () => new MillingProject(), () => false, TestServices.Version);
        try
        {
            await service.ConnectAsync(Settings);
            await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
            var program = service.LoadFile(Write("long.nc", Text));
            service.StartProgram();
            var total = program.Grbl.Count + 1;
            await WaitForAsync(() => service.RunJob is { Acknowledged: var acknowledged } && acknowledged == total - 1 && service.RunExecuting() == 50);
            await WaitForAsync(() => service.RunJob!.Elapsed.TotalSeconds >= MachineViewModel.MinSecondsForEstimate + 0.2);
            vm.Refresh();

            var executed = service.RunExecutedLines();
            Assert.Equal(program.Path.LineOf(49) + 1, executed);
            var match = TimeLeft().Match(vm.TimeText);
            Assert.True(match.Success, vm.TimeText);
            var left = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var expected = MachineViewModel.MinSecondsForEstimate * (total - executed) / executed;
            Assert.True(left >= Math.Floor(expected), $"{vm.TimeText}: at least {expected:0.0} s expected");
        }
        finally
        {
            vm.Close();
        }
    }
}
