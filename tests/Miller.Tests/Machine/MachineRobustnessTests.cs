using System.Diagnostics;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Machine;

// T-141 phase 6: what the controller may send that the protocol does not expect, and machines that
// do not do what they should. None of it may crash the controller thread, desynchronise the
// handshake or leave the machine moving: output without line ends, an ok nobody waits for, broken
// status reports, a restart in the middle of a program, a hold that never finishes, a reset that
// is never followed by the welcome line.
public sealed class MachineRobustnessTests
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 300);

    private static async Task<MachineController> ConnectAsync(FakeGrblLink link)
    {
        var controller = new MachineController(link, Fast);
        Assert.True(await controller.Ready.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await WaitForAsync(() => controller.Snapshot.Status.State == GrblStatus.Idle);
        return controller;
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

    private static MachineJob Program(int count)
        => MachineJob.Program(GrblProgram.Parse("p", string.Join('\n', Enumerable.Range(1, count).Select(k => $"G1 X{k} F500"))));

    [Fact]
    public async Task OutputWithoutLineEnds_IsCutAndTheLinkKeepsWorking()
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        link.Emit(new string('x', 100_000));
        controller.Run(Program(20));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        Assert.Equal(LinkState.Ready, controller.Snapshot.Link);
        Assert.Equal(1, link.MaxInFlight);
    }

    [Fact]
    public async Task AnOkNobodyWaitsFor_IsLogged_AndDoesNotAnswerTheNextLine()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(20) };
        using var controller = await ConnectAsync(link);
        link.Emit("ok");
        await WaitForAsync(() => controller.Log.Since(0).Any(e => e.Text.Contains("no line was waiting", StringComparison.Ordinal)));
        controller.Run(Program(10));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        Assert.Equal(1, link.MaxInFlight);
        Assert.Equal(11, link.Lines.Count);
    }

    [Theory]
    [InlineData("<Idle|MPos:a,b,c>")]
    [InlineData("<Idle|MPos:1,2>")]
    [InlineData("<Run|FS:x>")]
    [InlineData("<>")]
    public async Task ABrokenStatusReport_IsLogged_AndTheNextOneIsRead(string report)
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        link.Emit(report);
        link.Emit("error:99");
        link.Emit("ALARM:77");
        link.Emit("[MSG:Caution: Unlocked]");
        await WaitForAsync(() => controller.Log.Since(0).Any(e => e.Text.Contains("Caution", StringComparison.Ordinal)));
        Assert.Equal(LinkState.Ready, controller.Snapshot.Link);
        Assert.Contains(controller.Log.Since(0), e => e.Text == "error 99");
        Assert.Contains(controller.Log.Since(0), e => e.Text == "alarm 77");
        controller.Run(Program(3));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
    }

    [Fact]
    public async Task ARestartDuringAProgram_FailsIt_AndClearsTheLineInFlight()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(10) };
        using var controller = await ConnectAsync(link);
        controller.Run(Program(200));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 5 });
        link.Emit(FakeGrblLink.Banner);
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Failed });
        Assert.Equal(MachineController.UnexpectedResetMessage, controller.Snapshot.Job!.Message);
        var sent = link.Lines.Count;
        await WaitForAsync(() => controller.Snapshot.CanRun);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(sent, link.Lines.Count);
        controller.Run(Program(3));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done, Total: 4 });
    }

    [Fact]
    public async Task AHoldThatNeverFinishes_IsResetAfterTheHoldTimeout()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(5), HoldNeverCompletes = true };
        using var controller = await ConnectAsync(link);
        controller.Run(Program(500));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 5 });
        var watch = Stopwatch.StartNew();
        controller.Stop();
        await WaitForAsync(() => link.RealtimeBytes.Contains(GrblRealtime.SoftReset));
        Assert.InRange(watch.ElapsedMilliseconds, Fast.HoldTimeoutMs - 50, Fast.HoldTimeoutMs * 5);
        Assert.Equal(JobState.Stopped, controller.Snapshot.Job!.State);
        Assert.Contains(controller.Log.Since(0), e => e.Text.Contains("did not report a finished hold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResetWithoutAWelcomeLine_ClosesTheConnection()
    {
        var link = new FakeGrblLink { BannerAfterReset = false };
        using var controller = await ConnectAsync(link);
        controller.Reset();
        await WaitForAsync(() => controller.Snapshot.Link == LinkState.Closed);
        Assert.Contains("did not restart", controller.Snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLineIsSent_BetweenAResetAndTheWelcomeLine()
    {
        var link = new FakeGrblLink { BannerAfterReset = false };
        using var controller = await ConnectAsync(link);
        controller.Reset();
        await WaitForAsync(() => controller.Snapshot.LineInFlight);
        Assert.Throws<InvalidOperationException>(() => controller.Run(MachineJob.Commands("x", "$X")));
        link.Emit(FakeGrblLink.Banner);
        await WaitForAsync(() => controller.Snapshot.CanRun);
        controller.Run(MachineJob.Commands("x", "$X"));
        await WaitForAsync(() => link.Lines.Count == 1);
        Assert.Equal("$X", link.Lines[0]);
    }
}
