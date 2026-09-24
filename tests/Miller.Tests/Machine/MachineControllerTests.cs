using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Machine;

// T-140/T-141: the controller streams with the send-response protocol (never more than one line the
// controller has not answered, status polling beside it), stops a program on an error or an alarm
// with the line and the meaning, finishes only after the closing G4 P0, sends hold, resume and the
// stop sequence (hold, wait for Hold:0, soft reset) past the line queue, and closes the connection
// when the link fails or the controller stops answering, without letting anything escape its thread.
public sealed class MachineControllerTests
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

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = WaitMs)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.ElapsedMilliseconds < timeoutMs, "condition not reached in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    private static GrblProgram Program(int count)
        => GrblProgram.Parse("test", string.Join('\n', Enumerable.Range(1, count).Select(k => $"G1 X{k} Y{k % 7} F800")));

    [Fact]
    public async Task Connect_ByWelcomeLine_ReportsTheVersion()
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        Assert.Equal("1.1h", controller.Snapshot.Version);
        Assert.Equal(GrblStatus.Idle, controller.Snapshot.Status.State);
        Assert.Equal(new Axes(0.5, 1.5, 2.5), controller.Snapshot.Status.WorkPosition);
        Assert.True(controller.Snapshot.CanRun);
    }

    [Fact]
    public async Task Connect_WithoutWelcomeLine_IdentifiesByStatusReport()
    {
        var link = new FakeGrblLink(banner: false);
        using var controller = await ConnectAsync(link);
        Assert.Null(controller.Snapshot.Version);
        Assert.Equal(LinkState.Ready, controller.Snapshot.Link);
    }

    [Fact]
    public async Task Connect_SilentPort_FailsWithAMessage()
    {
        var link = new FakeGrblLink(banner: false) { AnswerStatus = false };
        using var controller = new MachineController(link, Fast);
        Assert.False(await controller.Ready.WaitAsync(TimeSpan.FromMilliseconds(WaitMs), TestContext.Current.CancellationToken));
        await WaitForAsync(() => link.IsDisposed);
        Assert.Equal(LinkState.Closed, controller.Snapshot.Link);
        Assert.Contains("No Grbl controller answered", controller.Snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_NeverHasMoreThanOneUnansweredLine()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(0.2) };
        using var controller = await ConnectAsync(link);
        var program = Program(1500);
        controller.Run(MachineJob.Program(program));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        Assert.Equal(1, link.MaxInFlight);
        Assert.Equal(program.Lines.Append(MachineJob.SyncLine), link.Lines);
        var job = controller.Snapshot.Job!;
        Assert.Equal((1501, 1501, 1501), (job.Total, job.Sent, job.Acknowledged));
        Assert.True(controller.Snapshot.CanRun);
    }

    [Fact]
    public async Task Program_IsDone_OnlyWhenTheSyncLineIsAnswered()
    {
        var link = new FakeGrblLink { Respond = line => line == MachineJob.SyncLine ? null : "ok" };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(5)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: 5, Sent: 6 });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Running, controller.Snapshot.Job!.State);
        link.Emit("ok");
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
    }

    [Fact]
    public async Task Error_StopsTheProgram_WithLineAndMeaning()
    {
        var link = new FakeGrblLink { Respond = line => line == "G1X3Y3F800" ? "error:20" : "ok" };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(6)));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Failed });
        var message = controller.Snapshot.Job!.Message!;
        Assert.Contains("Line 3", message, StringComparison.Ordinal);
        Assert.Contains("Unsupported or invalid g-code", message, StringComparison.Ordinal);
        Assert.Equal(3, controller.Snapshot.Job.SourceLine);
        Assert.Equal(3, link.Lines.Count);

        // The machine stops before the reset, so the position is kept.
        await WaitForAsync(() => link.RealtimeBytes.Contains(GrblRealtime.SoftReset));
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, link.RealtimeBytes);
        await WaitForAsync(() => controller.Snapshot.CanRun);
    }

    [Fact]
    public async Task Alarm_FailsTheJob_AndNoFurtherLineIsSent()
    {
        FakeGrblLink link = null!;
        link = new FakeGrblLink
        {
            OkDelay = TimeSpan.FromMilliseconds(20),
            Respond = line =>
            {
                if (line == "G1X4Y4F800")
                {
                    link.State = GrblStatus.Alarm;
                    link.Emit("ALARM:1");
                }

                return link.State == GrblStatus.Alarm && line != "G1X4Y4F800" ? "error:9" : "ok";
            },
        };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(20)));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Failed });
        Assert.Contains("Hard limit", controller.Snapshot.Job!.Message, StringComparison.Ordinal);
        await WaitForAsync(() => !controller.Snapshot.LineInFlight);
        Assert.Equal("G1X4Y4F800", link.Lines[^1]);
        Assert.Equal(GrblStatus.Alarm, controller.Snapshot.Status.State);
        Assert.Throws<InvalidOperationException>(() => controller.Run(MachineJob.Program(Program(2))));
        controller.Run(MachineJob.Commands("unlock", "$X"));
    }

    [Fact]
    public async Task HoldAndResume_LeaveWhileALineWaitsForItsAnswer()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(15) };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(40)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 3 });
        controller.Realtime(GrblRealtime.FeedHold);
        await WaitForAsync(() => link.RealtimeBytes.Contains(GrblRealtime.FeedHold));
        Assert.InRange(link.LinesAtFirstHold, 3, 20);
        controller.Realtime(GrblRealtime.CycleStart);
        controller.Realtime(GrblRealtime.FeedPlus10);
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.CycleStart, GrblRealtime.FeedPlus10 }, link.RealtimeBytes);
        Assert.Equal(1, link.MaxInFlight);
    }

    [Fact]
    public async Task Stop_HoldsWaitsForTheStopThenResets_AndSendsNoFurtherLine()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(5) };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(400)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 20 });
        controller.Stop();
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Stopped });
        Assert.Equal(MachineController.StoppedByUserMessage, controller.Snapshot.Job!.Message);
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, link.RealtimeBytes);
        Assert.Equal(link.LinesAtFirstHold, link.Lines.Count);
        Assert.True(controller.Snapshot.Job.Acknowledged < 400);
        await WaitForAsync(() => controller.Snapshot.CanRun);
    }

    [Fact]
    public async Task LinkFailure_ClosesTheConnection_AndFailsTheJob()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(2) };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(2000)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 10 });
        var watch = Stopwatch.StartNew();
        link.Fail();
        await WaitForAsync(() => controller.Snapshot.Link == LinkState.Closed);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 1000);
        Assert.Equal(JobState.Failed, controller.Snapshot.Job!.State);
        Assert.Contains("removed", controller.Snapshot.Error, StringComparison.Ordinal);
        await WaitForAsync(() => link.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => controller.Run(MachineJob.Commands("x", "$X")));
    }

    [Fact]
    public async Task Watchdog_ClosesAControllerThatStopsAnswering()
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        link.AnswerStatus = false;
        await WaitForAsync(() => controller.Snapshot.Link == LinkState.Closed, Fast.WatchdogMs * 4);
        Assert.Contains("stopped answering", controller.Snapshot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_RunsTheProgramInCheckMode_AndLeavesIt()
    {
        var link = new FakeGrblLink();
        using var controller = await ConnectAsync(link);
        var program = Program(3);
        controller.Run(MachineJob.Check(program));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        Assert.Equal(program.Lines.Prepend(MachineJob.CheckModeLine).Append(MachineJob.CheckModeLine), link.Lines);
        await WaitForAsync(() => controller.Snapshot.CanRun && controller.Snapshot.Status.State == GrblStatus.Idle);
        Assert.Empty(link.RealtimeBytes);
    }

    [Fact]
    public async Task Check_Error_ResetsOutOfCheckMode()
    {
        var link = new FakeGrblLink { Respond = line => line == "G1X2Y2F800" ? "error:22" : "ok" };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Check(Program(4)));
        await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Failed });
        Assert.Contains("Feed rate has not yet been set", controller.Snapshot.Job!.Message, StringComparison.Ordinal);
        await WaitForAsync(() => link.RealtimeBytes.Contains(GrblRealtime.SoftReset));
        Assert.Equal(GrblStatus.Idle, link.State);
        Assert.Equal(3, link.Lines.Count);
    }

    [Fact]
    public async Task Run_IsRefused_WhileBusyOrNotIdle()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(20) };
        using var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(50)));
        await WaitForAsync(() => controller.Snapshot.IsJobActive);
        var busy = Assert.Throws<InvalidOperationException>(() => controller.Run(MachineJob.Commands("jog", "$J=G91X1F100")));
        Assert.Equal(MachineController.JobActiveMessage, busy.Message);
        controller.Stop();
        await WaitForAsync(() => controller.Snapshot.CanRun);

        link.State = GrblStatus.Alarm;
        await WaitForAsync(() => controller.Snapshot.Status.State == GrblStatus.Alarm);
        var alarm = Assert.Throws<InvalidOperationException>(() => controller.Run(MachineJob.Program(Program(2))));
        Assert.Contains("Alarm", alarm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Realtime_AcceptsOnlyCallerRealtimeBytes()
    {
        using var controller = await ConnectAsync(new FakeGrblLink());
        Assert.Throws<ArgumentException>(() => controller.Realtime((byte)'G'));
        Assert.Throws<ArgumentException>(() => controller.Realtime(GrblRealtime.SoftReset));
        Assert.Throws<ArgumentException>(() => controller.Realtime(GrblRealtime.StatusQuery));
    }

    [Fact]
    public async Task Dispose_DuringAProgram_StopsTheMachineFirst()
    {
        var link = new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(5) };
        var controller = await ConnectAsync(link);
        controller.Run(MachineJob.Program(Program(1000)));
        await WaitForAsync(() => controller.Snapshot.Job is { Acknowledged: >= 5 });
        controller.Dispose();
        Assert.Equal(new[] { GrblRealtime.FeedHold, GrblRealtime.SoftReset }, link.RealtimeBytes);
        Assert.True(link.IsDisposed);
        Assert.Equal(JobState.Stopped, controller.Snapshot.Job!.State);
    }

    // The same streaming through a real socket: a loopback server answering like Grbl.
    [Fact]
    public async Task Program_OverTcp_ArrivesCompleteAndInOrder()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = Task.Run(() => ServeGrbl(listener, received), TestContext.Current.CancellationToken);
        var program = Program(300);
        using (var controller = new MachineController(TcpLink.Open(IPAddress.Loopback.ToString(), port), Fast))
        {
            Assert.True(await controller.Ready.WaitAsync(TimeSpan.FromMilliseconds(WaitMs), TestContext.Current.CancellationToken));
            await WaitForAsync(() => controller.Snapshot.Status.State == GrblStatus.Idle);
            controller.Run(MachineJob.Program(program));
            await WaitForAsync(() => controller.Snapshot.Job is { State: JobState.Done });
        }

        await server.WaitAsync(TimeSpan.FromMilliseconds(WaitMs), TestContext.Current.CancellationToken);
        Assert.Equal(program.Lines.Append(MachineJob.SyncLine), received);
    }

    private static void ServeGrbl(TcpListener listener, List<string> received)
    {
        using var client = listener.AcceptTcpClient();
        var stream = client.GetStream();
        void Send(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
            stream.Write(bytes);
        }

        Send(FakeGrblLink.Banner);
        var line = new StringBuilder();
        var buffer = new byte[256];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            for (var k = 0; k < count; k++)
            {
                var b = buffer[k];
                if (b == GrblRealtime.StatusQuery)
                {
                    Send("<Idle|MPos:0.000,0.000,0.000|FS:0,0>");
                }
                else if (b == '\n')
                {
                    received.Add(line.ToString());
                    line.Clear();
                    Send("ok");
                }
                else
                {
                    line.Append((char)b);
                }
            }
        }
    }
}
