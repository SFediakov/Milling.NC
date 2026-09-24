using System.Globalization;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;

namespace Miller.Application.Services;

public enum MachineConnectionKind
{
    Serial,
    Network,
}

public sealed record MachineConnectionSettings(MachineConnectionKind Kind, string SerialPort, int BaudRate, string Host, int NetworkPort);

// A program ready for the machine. SegmentsAfterLine maps the answered lines of a program made from
// the generated toolpath to the toolpath segments behind them, so the viewport can show the
// progress; it is null for a file from disk.
public sealed record MachineProgram(GrblProgram Grbl, string Source, IReadOnlyList<int>? SegmentsAfterLine, GrblBounds? Bounds)
{
    public bool FromToolpath => SegmentsAfterLine is not null;

    // Segments behind the first `acknowledged` lines of the job (the job adds G4 P0 at the end).
    public int SegmentsDone(int acknowledged)
        => SegmentsAfterLine is null || acknowledged <= 0 ? 0 : SegmentsAfterLine[Math.Min(acknowledged, SegmentsAfterLine.Count) - 1];
}

// The gate between the application and the machine cluster: opens the link the settings name,
// owns the controller of the current connection and one console log for the session, turns the
// generated toolpath or a file into a program and builds every command. Failures of the cluster
// reach the caller as MachineLinkException, GrblProgramException, ArgumentException or
// InvalidOperationException with a message for the user; the controller thread itself never throws.
public sealed class MachineService : IDisposable
{
    public const string GeneratedProgramName = "Generated toolpath";
    public const string NoProgramMessage = "No program is loaded.";
    public const string NoOutlineMessage = "The program has no XY moves to outline.";

    private readonly Func<MachineConnectionSettings, IMachineLink> _openLink;
    private readonly MachineTiming _timing;
    private MachineController? _controller;

    public MachineService()
        : this(OpenLink, MachineTiming.Default)
    {
    }

    public MachineService(Func<MachineConnectionSettings, IMachineLink> openLink, MachineTiming timing)
    {
        _openLink = openLink ?? throw new ArgumentNullException(nameof(openLink));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public MachineLog Log { get; } = new();

    public MachineProgram? Program { get; private set; }

    // Null before the first connection.
    public MachineSnapshot? Snapshot => _controller?.Snapshot;

    public bool IsConnected => _controller?.Snapshot.Link is LinkState.Connecting or LinkState.Ready;

    public static IMachineLink OpenLink(MachineConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Kind == MachineConnectionKind.Serial
            ? SerialLink.Open(settings.SerialPort, settings.BaudRate)
            : TcpLink.Open(settings.Host, settings.NetworkPort);
    }

    public static IReadOnlyList<string> SerialPorts() => SerialLink.List();

    // Opens the link off the calling thread and waits for the controller to identify itself.
    public async Task ConnectAsync(MachineConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsConnected)
        {
            throw new InvalidOperationException("The machine is already connected.");
        }

        _controller?.Dispose();
        _controller = null;
        var link = await Task.Run(() => _openLink(settings));
        var controller = new MachineController(link, _timing, Log);
        _controller = controller;
        if (!await controller.Ready)
        {
            var error = controller.Snapshot.Error ?? $"{link.Name} closed before the controller answered.";
            throw new MachineLinkException(error);
        }
    }

    // A running program is stopped first (hold, then reset) so the machine is never left moving.
    public void Disconnect() => _controller?.Dispose();

    public void Dispose() => Disconnect();

    public MachineProgram LoadToolpath(Toolpath toolpath, MillingProject project, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(project);
        var post = PostProcessorRegistry.GetById(project.PostProcessorId);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        post.Write(toolpath, project, appVersion, writer);
        var grbl = GrblProgram.Parse(GeneratedProgramName, writer.ToString());
        Program = new MachineProgram(grbl, GeneratedProgramName, SegmentMap(grbl, toolpath.Count), GrblBounds.Of(grbl));
        return Program;
    }

    public MachineProgram LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var grbl = GrblProgram.Parse(Path.GetFileName(path), File.ReadAllText(path));
        Program = new MachineProgram(grbl, path, null, GrblBounds.Of(grbl));
        return Program;
    }

    public void ClearProgram() => Program = null;

    public void StartProgram() => Controller.Run(MachineJob.Program(RequireProgram().Grbl));

    public void CheckProgram() => Controller.Run(MachineJob.Check(RequireProgram().Grbl));

    public void Outline()
    {
        var bounds = RequireProgram().Bounds ?? throw new InvalidOperationException(NoOutlineMessage);
        Controller.Run(MachineJob.Commands("Outline", GrblCommands.Outline(bounds)));
    }

    public void Pause() => Controller.Realtime(GrblRealtime.FeedHold);

    public void Resume() => Controller.Realtime(GrblRealtime.CycleStart);

    public void Stop() => Controller.Stop();

    public void Reset() => Controller.Reset();

    public void Realtime(byte command) => Controller.Realtime(command);

    public void Home() => Controller.Run(MachineJob.Commands("Home", GrblCommands.Home));

    public void Unlock() => Controller.Run(MachineJob.Commands("Unlock", GrblCommands.Unlock));

    public void Jog(MachineAxis axis, double distance, double feed)
        => Controller.Run(MachineJob.Commands("Jog", GrblCommands.Jog(axis, distance, feed)));

    public void CancelJog() => Controller.Realtime(GrblRealtime.JogCancel);

    public void Zero(bool x, bool y, bool z) => Controller.Run(MachineJob.Commands("Zero", GrblCommands.Zero(x, y, z)));

    public void GoToXyZero() => Controller.Run(MachineJob.Commands("Go to zero", GrblCommands.GoToXyZero));

    public void ProbeZ(double plateThickness, double maxTravel, double feed, double retract)
        => Controller.Run(MachineJob.Commands("Probe Z", GrblCommands.ProbeZ(plateThickness, maxTravel, feed, retract)));

    // A console line. ! and ~ typed alone are the realtime hold and resume; ? prints the last status.
    public void Send(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();
        if (trimmed.Length == 1 && trimmed[0] is '!' or '~')
        {
            Controller.Realtime((byte)trimmed[0]);
            return;
        }

        if (trimmed == "?")
        {
            var status = Controller.Snapshot.Status;
            Log.Add(LogKind.Info, string.Create(CultureInfo.InvariantCulture,
                $"{status.State} WPos {status.WorkPosition.X:0.000},{status.WorkPosition.Y:0.000},{status.WorkPosition.Z:0.000} MPos {status.MachinePosition.X:0.000},{status.MachinePosition.Y:0.000},{status.MachinePosition.Z:0.000}"));
            return;
        }

        Controller.Run(MachineJob.Commands("Console", trimmed));
    }

    private MachineController Controller
        => _controller is { Snapshot.Link: LinkState.Ready } controller ? controller : throw new InvalidOperationException(MachineController.NotConnectedMessage);

    private MachineProgram RequireProgram() => Program ?? throw new InvalidOperationException(NoProgramMessage);

    // The post-processor writes one motion line per toolpath segment, after the moves that bring the
    // tool to the start; those lead-in moves are the motion lines beyond the segment count.
    private static IReadOnlyList<int> SegmentMap(GrblProgram program, int segments)
    {
        var motion = program.Lines.Select(l => l.IndexOfAny(new[] { 'X', 'Y', 'Z' }) >= 0).ToList();
        var leadIn = motion.Count(m => m) - segments;
        if (leadIn < 0)
        {
            throw new InvalidOperationException($"The program has fewer motion lines than the toolpath has segments ({segments}).");
        }

        var map = new int[program.Count];
        var seen = 0;
        for (var k = 0; k < map.Length; k++)
        {
            seen += motion[k] ? 1 : 0;
            map[k] = Math.Clamp(seen - leadIn, 0, segments);
        }

        return map;
    }
}
