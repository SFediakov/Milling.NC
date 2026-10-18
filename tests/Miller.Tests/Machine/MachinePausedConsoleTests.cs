using System.Diagnostics;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Machine;

// T-202 to T-204 in the controller: a line typed while a program is paused goes out before the next
// program line and never counts as program progress (its error does not fail the program); a command
// job waits its delay after every answer; connecting writes nothing but status queries; the serial
// port is opened without the DTR rise that restarts an Arduino.
public sealed class MachinePausedConsoleTests
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private const int WaitMs = 10_000;

    private static async Task<MachineController> ConnectAsync(FakeGrblLink link)
    {
        var controller = new MachineController(link, Fast);
        Assert.True(await controller.Ready.WaitAsync(TimeSpan.FromMilliseconds(WaitMs), TestContext.Current.CancellationToken));
        await WaitForAsync(() => controller.Snapshot.IsReady && controller.Snapshot.Status.State != GrblStatus.UnknownState);
        return controller;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < WaitMs, "condition not reached in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    private static GrblProgram Program(int count)
        => GrblProgram.Parse("test", string.Join('\n', Enumerable.Range(1, count).Select(k => $"G1 X{k} F800")));

    [Fact]
    public async Task PausedProgram_TakesALineBeforeItsNextLine_WithoutCountingIt()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(10) };
        using var controller = await ConnectAsync(link);
        var program = Program(100);
        Assert.Equal(MachineController.NotPausedMessage, Assert.Throws<InvalidOperationException>(() => controller.Interject("G0 Z10")).Message);

        controller.Run(MachineJob.Program(program));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 3 });
        Assert.Equal(MachineController.NotPausedMessage, Assert.Throws<InvalidOperationException>(() => controller.Interject("G0 Z10")).Message);

        controller.Realtime(GrblRealtime.FeedHold);
        await WaitForAsync(() => controller.Snapshot.Status.IsReport(GrblStatus.Hold));
        Assert.Throws<GrblProgramException>(() => controller.Interject("G0 Z10 !"));
        controller.Interject("g0 z10");
        await WaitForAsync(() => link.Lines.Contains("G0Z10"));
        controller.Realtime(GrblRealtime.CycleStart);
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });

        var job = controller.Snapshot.Job!;
        Assert.Equal(program.Count + 1, job.Acknowledged);
        Assert.Equal(program.Count + 2, link.Lines.Count);
        var at = link.Lines.IndexOf("G0Z10");
        Assert.Equal(program.Lines.Take(at), link.Lines.Take(at));
        Assert.Equal(program.Lines.Skip(at).Append(MachineJob.SyncLine), link.Lines.Skip(at + 1));
        Assert.Equal(1, link.MaxInFlight);
        Assert.Contains(controller.Log.Since(0), e => e.Kind == LogKind.Sent && e.Text == "G0Z10");
    }

    [Fact]
    public async Task AnErrorToAPausedLine_IsLogged_AndTheProgramGoesOn()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(10), Respond = line => line == "G0Z10" ? "error:20" : "ok" };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(100)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 3 });
        controller.Realtime(GrblRealtime.FeedHold);
        await WaitForAsync(() => controller.Snapshot.Status.IsReport(GrblStatus.Hold));
        controller.Interject("G0 Z10");
        await WaitForAsync(() => controller.Log.Since(0).Any(e => e.Kind == LogKind.Error && e.Text.StartsWith("G0Z10:", StringComparison.Ordinal)));
        controller.Realtime(GrblRealtime.CycleStart);
        await WaitForAsync(() => controller.Snapshot.Job is { IsActive: false });
        Assert.Equal(JobState.Done, controller.Snapshot.Job!.State);
    }

    [Fact]
    public async Task CommandJob_WaitsItsDelayAfterEveryAnswer()
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        const int delay = 120;
        controller.Run(MachineJob.Commands("macro", delay, new[] { "M3 S1000", "G4 P0", "M5" }));
        await WaitForAsync(() => controller.Snapshot.Job is { IsActive: false });
        var job = controller.Snapshot.Job!;
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(new[] { "M3S1000", "G4P0", "M5" }, link.Lines);
        Assert.InRange(job.Elapsed.TotalMilliseconds, 2 * delay, 2 * delay + 2000);
        Assert.Equal(0, MachineJob.Commands("x", "G0 X1").DelayMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => MachineJob.Commands("x", -1, new[] { "G0 X1" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MachineJob.Commands("x", MachineJob.MaxDelayMs + 1, new[] { "G0 X1" }));
    }

    [Fact]
    public async Task Connect_WritesNothingButStatusQueries()
    {
        var link = new FakeGrblLink(banner: false);
        using var controller = await ConnectAsync(link);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(link.Lines);
        Assert.Empty(link.RealtimeBytes);
    }

    [Fact]
    public void SerialPort_OpensWithoutRaisingDtrOrRts_AndWithoutHangUpOnClose()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Miller.Machine.Native", "src", "ms_serial.c")));
        Assert.Contains("dcb.fDtrControl = DTR_CONTROL_DISABLE;", source, StringComparison.Ordinal);
        Assert.Contains("dcb.fRtsControl = RTS_CONTROL_DISABLE;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_CONTROL_ENABLE", source, StringComparison.Ordinal);
        Assert.Contains("tty.c_cflag &= ~(tcflag_t)(CSIZE | PARENB | CSTOPB | CRTSCTS | HUPCL);", source, StringComparison.Ordinal);
    }
}
