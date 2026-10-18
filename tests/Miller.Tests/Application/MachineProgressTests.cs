using System.Diagnostics;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-198 to T-200 at the machine gate: work coordinates go out as G10 L20; a program's own moves become
// the machine view's path with a line map; the progress of the latest run survives later commands and
// a reconnection, and Save progress writes the resume program to a new file, never while the run goes
// on and never over the program file.
public sealed class MachineProgressTests : IDisposable
{
    private static readonly MachineTiming Fast = new(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000);
    private static readonly MachineConnectionSettings Settings = new(MachineConnectionKind.Serial, "fake", SerialLink.DefaultBaudRate, string.Empty, TcpLink.DefaultPort);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-progress-{Guid.NewGuid():N}");

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

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ProgramPath_HasOneSegmentPerTargetLine_WithKindsAndALineMap()
    {
        using var service = new MachineService(_ => new FakeGrblLink(), Fast);
        var program = service.LoadFile(Write("p.nc", "( p )\nG21 G90\nG0 Z5\nG0 X10 Y0\nG1 Z-1 F200\nG1 X20 Y5 F800\nM5\n"));
        var path = program.Path;
        Assert.Equal(4, path.Segments.Count);
        Assert.Equal(new ToolpathSegment(Vector3.Zero, new Vector3(0, 0, 5), MoveKind.Rapid, 0), path.Segments[0]);
        Assert.Equal(MoveKind.Rapid, path.Segments[1].Kind);
        Assert.Equal(new ToolpathSegment(new Vector3(10, 0, 5), new Vector3(10, 0, -1), MoveKind.Plunge, 200), path.Segments[2]);
        Assert.Equal(new ToolpathSegment(new Vector3(10, 0, -1), new Vector3(20, 5, -1), MoveKind.Feed, 800), path.Segments[3]);
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4, 4, 4 }, Enumerable.Range(0, 8).Select(path.SegmentsDone));
        Assert.Equal(4, path.SegmentsDone(program.Grbl.Count + 1));
    }

    [Fact]
    public async Task SetWork_SendsTheValuesOfTheNamedAxes()
    {
        var link = new FakeGrblLink();
        using var service = new MachineService(_ => link, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        service.SetWork(1.5, null, -0.25);
        await WaitForAsync(() => link.Lines.Count == 1 && service.Snapshot!.CanRun);
        Assert.Equal("G10L20P0X1.5Z-0.25", link.Lines[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetWork(double.NaN, null, null));
        Assert.Single(link.Lines);
    }

    [Fact]
    public async Task Progress_SurvivesLaterCommandsAndAReconnection_AndIsSavedAsAResumeProgram()
    {
        var links = new List<FakeGrblLink>();
        using var service = new MachineService(_ =>
        {
            links.Add(new FakeGrblLink { OkDelay = TimeSpan.FromMilliseconds(5) });
            return links[^1];
        }, Fast);
        await service.ConnectAsync(Settings);
        await WaitForAsync(() => service.Snapshot!.Status.State == GrblStatus.Idle);
        var source = Write("long.nc", "G21 G90\nG0 Z5\n" + string.Concat(Enumerable.Range(1, 300).Select(k => $"G1 X{k} Y0 Z-1 F900\n")) + "M30\n");
        var output = Path.Combine(_root, "long_resume.nc");
        Assert.Null(service.RunJob);
        Assert.Equal(MachineService.NoRunMessage, Assert.Throws<InvalidOperationException>(() => service.SaveProgress(output)).Message);

        var program = service.LoadFile(source);
        service.StartProgram();
        Assert.Same(program, service.RunProgram);
        await WaitForAsync(() => service.RunJob is { Acknowledged: >= 10 });
        Assert.Equal(MachineService.RunActiveMessage, Assert.Throws<InvalidOperationException>(() => service.SaveProgress(output)).Message);

        service.Stop();
        await WaitForAsync(() => service.Snapshot!.Job is { State: JobState.Stopped } && service.Snapshot.CanRun && service.Snapshot.Status.State == GrblStatus.Idle);
        var executed = service.RunJob!.Acknowledged;
        Assert.InRange(executed, 10, program.Grbl.Count - 1);

        // A jog replaces the snapshot's job; the run's progress stays.
        service.Jog(MachineAxis.Z, 1, 100);
        await WaitForAsync(() => service.Snapshot!.Job is { Kind: MachineJobKind.Command, IsActive: false });
        Assert.Equal(executed, service.RunJob!.Acknowledged);

        service.SaveProgress(output);
        Assert.Equal(GrblResume.Build(program.Grbl, executed), File.ReadAllText(output));
        Assert.Equal(MachineService.SameFileMessage, Assert.Throws<InvalidOperationException>(() => service.SaveProgress(source)).Message);

        service.Disconnect();
        await service.ConnectAsync(Settings);
        Assert.Equal(2, links.Count);
        Assert.Equal(executed, service.RunJob!.Acknowledged);

        // Another loaded program does not change what ran.
        service.LoadFile(Write("other.nc", "G0 Z1\nG0 X1 Y1\n"));
        Assert.Same(program, service.RunProgram);
    }
}
