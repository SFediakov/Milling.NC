using System.Diagnostics;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-142/T-143: the machine gate connects through the link the settings name and reports a failed
// connection as MachineLinkException; every command reaches the controller as its Grbl line and is
// refused while disconnected or while a job runs; the generated toolpath becomes a program whose
// answered lines map onto toolpath segments; a file with a bad line is refused with its number; the
// console sends lines through the same handshake; disconnecting during a program stops the machine.
public sealed class MachineServiceTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private static readonly MachineConnectionSettings Settings = new(MachineConnectionKind.Serial, "fake", SerialLink.DefaultBaudRate, string.Empty, TcpLink.DefaultPort);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-machine-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < 10_000, "condition not reached in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<(MachineService Service, FakeGrblLink Link)> ConnectedAsync()
    {
        var link = new FakeGrblLink();
        var service = new MachineService(_ => link, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        return (service, link);
    }

    // Waits for the job the command starts, not for the one before it.
    private static async Task RunAsync(MachineService service, Action command)
    {
        var before = service.Snapshot!.Job?.StartTimestamp;
        command();
        await WaitForAsync(() => service.Snapshot!.Job is { IsActive: false } job && job.StartTimestamp != before && !service.Snapshot.LineInFlight);
    }

    private static Toolpath Square()
    {
        var toolpath = new Toolpath();
        var corners = new[] { new Vector3(0, 0, 1), new Vector3(10, 0, 1), new Vector3(10, 10, 1), new Vector3(0, 10, 1), new Vector3(0, 0, 1) };
        toolpath.Add(new ToolpathSegment(new Vector3(0, 0, 5), corners[0], MoveKind.Plunge, 200));
        for (var k = 1; k < corners.Length; k++)
        {
            toolpath.Add(new ToolpathSegment(corners[k - 1], corners[k], MoveKind.Feed, 800));
        }

        toolpath.Add(new ToolpathSegment(corners[^1], new Vector3(0, 0, 5), MoveKind.Rapid, 3000));
        return toolpath;
    }

    [Fact]
    public async Task Commands_ReachTheControllerAsGrblLines()
    {
        var (service, link) = await ConnectedAsync();
        using var _ = service;
        Assert.True(service.IsConnected);
        await RunAsync(service, service.Home);
        await RunAsync(service, service.Unlock);
        await RunAsync(service, () => service.Jog(MachineAxis.Y, -0.1, 300));
        await RunAsync(service, () => service.Zero(true, false, true));
        await RunAsync(service, service.GoToXyZero);
        await RunAsync(service, () => service.ProbeZ(10, 15, 40, 3));
        await RunAsync(service, () => service.Send("$$"));
        Assert.Equal(new[] { "$H", "$X", "$J=G91G21Y-0.1F300", "G10L20P0X0Z0", "G90G0X0Y0", "G21G91G38.2Z-15F40", "G10L20P0Z10", "G0Z3", "G90", "$$" }, link.Lines);

        service.CancelJog();
        service.Realtime(GrblRealtime.FeedPlus10);
        service.Send("!");
        service.Send(" ~ ");
        await WaitForAsync(() => link.RealtimeBytes.Count == 4);
        Assert.Equal(new[] { GrblRealtime.JogCancel, GrblRealtime.FeedPlus10, GrblRealtime.FeedHold, GrblRealtime.CycleStart }, link.RealtimeBytes);
        Assert.Contains(service.Log.Since(0), e => e.Kind == LogKind.Sent && e.Text == "$H");
    }

    [Fact]
    public async Task Commands_AreRefused_WhileDisconnectedOrBusy()
    {
        using var idle = new MachineService(_ => new FakeGrblLink(), Fast);
        Assert.False(idle.IsConnected);
        var refused = Assert.Throws<InvalidOperationException>(idle.Home);
        Assert.Equal(MachineController.NotConnectedMessage, refused.Message);
        Assert.Throws<InvalidOperationException>(idle.Pause);

        var (service, link) = await ConnectedAsync();
        using var _ = service;
        link.OkDelay = TimeSpan.FromMilliseconds(20);
        service.LoadToolpath(Square(), new MillingProject(), TestServices.Version);
        service.StartProgram();
        await WaitForAsync(() => service.Snapshot!.IsJobActive);
        Assert.Equal(MachineController.JobActiveMessage, Assert.Throws<InvalidOperationException>(() => service.Jog(MachineAxis.X, 1, 100)).Message);
        Assert.Throws<InvalidOperationException>(() => service.Send("G0 X5"));
        Assert.Throws<InvalidOperationException>(service.CheckProgram);
        service.Pause();
        service.Resume();
        service.Realtime(GrblRealtime.SpindleMinus10);
        await WaitForAsync(() => service.Snapshot!.Job is { State: JobState.Done });
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.CycleStart, GrblRealtime.SpindleMinus10 }, link.RealtimeBytes);
    }

    [Fact]
    public async Task GeneratedToolpath_MapsAnsweredLinesToSegments()
    {
        using var service = new MachineService(_ => new FakeGrblLink(), Fast);
        var toolpath = Square();
        var program = service.LoadToolpath(toolpath, new MillingProject(), TestServices.Version);
        Assert.True(program.FromToolpath);
        Assert.Equal(MachineService.GeneratedProgramName, program.Grbl.Name);
        Assert.Equal(new GrblBounds(0, 0, 10, 10), program.Bounds);

        // G21 G90 G94 G17, S M3, G0 Z, G0 X Y lead in; one line per segment; M5 and M30 after them.
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 6, 6 }, program.SegmentsAfterLine);
        Assert.Equal(0, program.SegmentsDone(0));
        Assert.Equal(2, program.SegmentsDone(6));
        Assert.Equal(toolpath.Count, program.SegmentsDone(program.Grbl.Count + 1));

        var (connected, link) = await ConnectedAsync();
        using var _ = connected;
        connected.LoadToolpath(toolpath, new MillingProject(), TestServices.Version);
        connected.StartProgram();
        await WaitForAsync(() => connected.Snapshot!.Job is { State: JobState.Done });
        Assert.Equal(program.Grbl.Lines.Append(MachineJob.SyncLine), link.Lines);
        Assert.Equal(1, link.MaxInFlight);
    }

    [Fact]
    public async Task FileProgram_CheckAndOutline()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "part.nc");
        File.WriteAllText(path, "( part )\nG21 G90\nG0 X-5 Y2\nG1 X15 Y12 F500\nM30\n");
        var (service, link) = await ConnectedAsync();
        using var _ = service;
        var program = service.LoadFile(path);
        Assert.False(program.FromToolpath);
        Assert.Equal(0, program.SegmentsDone(3));
        Assert.Equal("part.nc", program.Grbl.Name);

        await RunAsync(service, service.CheckProgram);
        Assert.Equal(JobState.Done, service.Snapshot!.Job!.State);
        await WaitForAsync(() => service.Snapshot!.CanRun && service.Snapshot.Status.State == GrblStatus.Idle);
        await RunAsync(service, service.Outline);
        Assert.Equal(new[] { "$C", "G21G90", "G0X-5Y2", "G1X15Y12F500", "M30", "$C", "G21G90G0X-5Y2", "G0X15Y2", "G0X15Y12", "G0X-5Y12", "G0X-5Y2" }, link.Lines);

        File.WriteAllText(path, "G0 X1\nG0 X2 ; fine\nG0 X3 !\n");
        var bad = Assert.Throws<GrblProgramException>(() => service.LoadFile(path));
        Assert.Equal(3, bad.Line);
        Assert.Same(program, service.Program);
        service.ClearProgram();
        Assert.Equal(MachineService.NoProgramMessage, Assert.Throws<InvalidOperationException>(service.StartProgram).Message);
    }

    [Fact]
    public async Task FailedConnection_IsReportedWithItsReason()
    {
        using var unreachable = new MachineService(_ => throw new MachineLinkException("COM9 cannot be opened (no such port)."), Fast);
        var open = await Assert.ThrowsAsync<MachineLinkException>(() => unreachable.ConnectAsync(Settings));
        Assert.Contains("COM9", open.Message, StringComparison.Ordinal);
        Assert.False(unreachable.IsConnected);

        using var silent = new MachineService(_ => new FakeGrblLink(banner: false) { AnswerStatus = false }, Fast);
        var mute = await Assert.ThrowsAsync<MachineLinkException>(() => silent.ConnectAsync(Settings));
        Assert.Contains("No Grbl controller answered", mute.Message, StringComparison.Ordinal);
        Assert.False(silent.IsConnected);
        Assert.Contains(silent.Log.Since(0), e => e.Kind == LogKind.Error);

        var (service, _) = await ConnectedAsync();
        using var __ = service;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Settings));
    }

    [Fact]
    public async Task Disconnect_DuringAProgram_StopsTheMachine()
    {
        var links = new List<FakeGrblLink>();
        var service = new MachineService(_ =>
        {
            links.Add(new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(5) });
            return links[^1];
        }, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        var link = links[0];
        service.LoadFile(WriteProgram(400));
        service.StartProgram();
        await WaitForAsync(() => service.Snapshot!.Job is { Acknowledged: >= 5 });
        service.Disconnect();
        Assert.False(service.IsConnected);
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, link.RealtimeBytes);
        Assert.True(link.IsDisposed);
        Assert.Equal(JobState.Stopped, service.Snapshot!.Job!.State);

        // A new connection starts from a fresh controller and keeps the console history.
        var entries = service.Log.Since(0).Count;
        await service.ConnectAsync(Settings with { SerialPort = "again" });
        Assert.True(service.IsConnected);
        Assert.Equal(2, links.Count);
        Assert.True(service.Log.Since(0).Count > entries);
        service.Dispose();
        Assert.True(links[1].IsDisposed);
    }

    private string WriteProgram(int count)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "long.nc");
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(k => $"G1 X{k} F900"));
        return path;
    }
}
