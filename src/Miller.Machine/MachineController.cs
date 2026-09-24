using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Miller.Machine.Grbl;
using Miller.Machine.Links;

namespace Miller.Machine;

// Streams G-code to a Grbl controller with the send-response protocol: a line is written only after
// the controller answered the previous one with ok or error, so it never holds more than one line
// it has not confirmed. Grbl's ok means the line was executed (a move is then in its planner), so
// motion stays continuous; a program ends with G4 P0, whose ok comes only when every move is done.
//
// One thread owns the link: public methods post work to it and read the published snapshot, so no
// caller waits on the machine. Realtime bytes (hold, resume, overrides) go out at the next turn of
// the loop, at most one read timeout later, whatever line is waiting. A failed link, a controller
// that stops answering status queries or an internal fault closes the connection and fails the
// running job; nothing escapes the thread.
public sealed class MachineController : IDisposable
{
    public const string NotConnectedMessage = "The machine is not connected.";
    public const string JobActiveMessage = "A job is running on the machine.";
    public const string LineInFlightMessage = "The controller has not answered the last line yet.";
    public const string StoppedByUserMessage = "Stopped by the user.";
    public const string StoppedByResetMessage = "Stopped by a soft reset.";
    public const string UnexpectedResetMessage = "The controller restarted while the job was running.";
    public const string DisconnectedMessage = "The connection was closed while the job was running.";

    private const int BufferSize = 4096;
    private const int MaxLineBuffer = 1024;
    private const int DisposeMarginMs = 1000;
    private static readonly string[] BannerPrefixes = { "Grbl ", "GrblHAL " };

    private readonly IMachineLink _link;
    private readonly MachineTiming _timing;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ConcurrentQueue<byte> _realtime = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _incoming = new();
    private MachineSnapshot _snapshot;
    private volatile bool _closing;

    // Owned by the I/O thread.
    private LinkState _linkState = LinkState.Connecting;
    private bool _identified;
    private string? _version;
    private string? _error;
    private GrblStatus _status = GrblStatus.Unknown;
    private MachineJob? _job;
    private JobState _jobState;
    private int _sent;
    private int _acknowledged;
    private int _sourceLine;
    private long _jobStart;
    private long _jobEnd;
    private string? _jobMessage;
    private int _outstanding = -1;
    private bool _awaitingBanner;
    private long _awaitingBannerSince;
    private bool _stopping;
    private long _stopDeadline;
    private long _connectedAt;
    private long _lastStatusAt;
    private long _nextPollAt;

    // The log may outlive the controller, so a console keeps its history over reconnections.
    public MachineController(IMachineLink link, MachineTiming? timing = null, MachineLog? log = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _timing = timing ?? MachineTiming.Default;
        Log = log ?? new MachineLog();
        _snapshot = new MachineSnapshot(LinkState.Connecting, link.Name, null, GrblStatus.Unknown, null, false, null);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Miller machine I/O" };
        _thread.Start();
    }

    public MachineLog Log { get; }

    public MachineSnapshot Snapshot => Volatile.Read(ref _snapshot);

    // True once the controller identified itself (welcome line or status report), false when the
    // connection closed first.
    public Task<bool> Ready => _ready.Task;

    public void Run(MachineJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var refusal = Refusal(Snapshot.IsReady, Snapshot.IsJobActive, Snapshot.LineInFlight, Snapshot.Status, job.Kind);
        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }

        Post(() =>
        {
            var late = Refusal(_linkState == LinkState.Ready, IsJobActive, _outstanding >= 0 || _awaitingBanner, _status, job.Kind);
            if (late is not null)
            {
                Log.Add(LogKind.Error, $"{job.Name} not started: {late}");
                return;
            }

            StartJob(job);
        });
    }

    // Hold, resume, jog cancel, overrides and coolant; the soft reset has Reset and the status
    // query is sent by the controller thread itself.
    public void Realtime(byte command)
    {
        if (!GrblRealtime.IsRealtime(command) || command is GrblRealtime.SoftReset or GrblRealtime.StatusQuery)
        {
            throw new ArgumentException($"0x{command:X2} is not a realtime command a caller may send.", nameof(command));
        }

        _realtime.Enqueue(command);
    }

    // Feed hold, then the soft reset once the machine has stopped (or the hold timeout passed), so
    // the position is kept and the spindle stops; the running job ends as Stopped.
    public void Stop() => Post(BeginStop);

    public void Reset() => Post(() => SoftReset(StoppedByResetMessage));

    public void Dispose()
    {
        if (_closing)
        {
            return;
        }

        if (Snapshot.IsJobActive)
        {
            Stop();
            var deadline = Stopwatch.GetTimestamp() + Ticks(_timing.HoldTimeoutMs + DisposeMarginMs);
            while (Snapshot.IsJobActive && Stopwatch.GetTimestamp() < deadline && _thread.IsAlive)
            {
                Thread.Sleep(_timing.ReadTimeoutMs);
            }
        }

        _closing = true;
        _thread.Join(_timing.ReadTimeoutMs + DisposeMarginMs);
    }

    private static string? Refusal(bool ready, bool jobActive, bool lineInFlight, GrblStatus status, MachineJobKind kind)
    {
        if (!ready)
        {
            return NotConnectedMessage;
        }

        if (jobActive)
        {
            return JobActiveMessage;
        }

        if (lineInFlight)
        {
            return LineInFlightMessage;
        }

        return kind != MachineJobKind.Command && !status.IsReport(GrblStatus.Idle)
            ? $"The machine is {status.State}; a program starts only when it is Idle."
            : null;
    }

    private static long Ticks(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private bool IsJobActive => _job is not null && _jobState is JobState.Running or JobState.Stopping;

    private void Post(Action action) => _commands.Enqueue(action);

    private void Loop()
    {
        var buffer = new byte[BufferSize];
        _connectedAt = Stopwatch.GetTimestamp();
        _nextPollAt = _connectedAt + Ticks(_timing.BannerWaitMs);
        try
        {
            while (!_closing && _linkState != LinkState.Closed)
            {
                while (_commands.TryDequeue(out var command))
                {
                    command();
                }

                FlushRealtime();
                PumpLine();
                Tick(Stopwatch.GetTimestamp());
                Publish();
                if (_linkState == LinkState.Closed)
                {
                    break;
                }

                var count = _link.Read(buffer, _timing.ReadTimeoutMs);
                if (count > 0)
                {
                    Receive(buffer.AsSpan(0, count));
                }
            }
        }
        catch (MachineLinkException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail($"Machine connection fault: {ex.Message}");
        }
        finally
        {
            _link.Dispose();
            if (IsJobActive)
            {
                FinishJob(JobState.Failed, _error ?? DisconnectedMessage);
            }

            _linkState = LinkState.Closed;
            _ready.TrySetResult(false);
            Publish();
        }
    }

    private void Fail(string message)
    {
        _error = message;
        _linkState = LinkState.Closed;
        Log.Add(LogKind.Error, message);
        if (IsJobActive)
        {
            FinishJob(JobState.Failed, message);
        }

        _ready.TrySetResult(false);
    }

    private void Tick(long now)
    {
        if (!_identified && now - _connectedAt > Ticks(_timing.IdentifyTimeoutMs))
        {
            Fail($"No Grbl controller answered on {_link.Name} within {_timing.IdentifyTimeoutMs} ms.");
            return;
        }

        if (_identified && now - _lastStatusAt > Ticks(_timing.WatchdogMs))
        {
            Fail($"The controller stopped answering: no status report for {_timing.WatchdogMs} ms.");
            return;
        }

        if (_awaitingBanner && now - _awaitingBannerSince > Ticks(_timing.IdentifyTimeoutMs))
        {
            Fail($"The controller did not restart within {_timing.IdentifyTimeoutMs} ms of the soft reset.");
            return;
        }

        if (_stopping && now >= _stopDeadline)
        {
            Log.Add(LogKind.Info, $"The machine did not report a finished hold within {_timing.HoldTimeoutMs} ms; resetting.");
            SoftReset(StoppedByUserMessage);
        }

        if (now >= _nextPollAt)
        {
            _link.Write(stackalloc byte[] { GrblRealtime.StatusQuery });
            _nextPollAt = now + Ticks(_timing.PollMs);
        }
    }

    private void FlushRealtime()
    {
        if (_realtime.IsEmpty)
        {
            return;
        }

        var bytes = new List<byte>();
        while (_realtime.TryDequeue(out var command))
        {
            bytes.Add(command);
        }

        _link.Write(bytes.ToArray());
        foreach (var command in bytes)
        {
            Log.Add(LogKind.Sent, command < 0x80 ? ((char)command).ToString() : $"0x{command:X2}");
        }
    }

    private void PumpLine()
    {
        if (_outstanding >= 0 || _awaitingBanner || _job is null || _jobState != JobState.Running || _sent >= _job.Count)
        {
            return;
        }

        var line = _job.Lines[_sent];
        _link.Write(Encoding.ASCII.GetBytes(line + "\n"));
        _outstanding = _sent;
        _sent++;
        if (_job.Kind == MachineJobKind.Command)
        {
            Log.Add(LogKind.Sent, line);
        }
    }

    private void StartJob(MachineJob job)
    {
        _job = job;
        _jobState = JobState.Running;
        _sent = 0;
        _acknowledged = 0;
        _sourceLine = 0;
        _jobStart = Stopwatch.GetTimestamp();
        _jobEnd = 0;
        _jobMessage = null;
        if (job.Kind != MachineJobKind.Command)
        {
            Log.Add(LogKind.Info, $"{job.Kind} started: {job.Name}, {job.Count} lines");
        }
    }

    private void FinishJob(JobState state, string? message)
    {
        _jobState = state;
        _jobEnd = Stopwatch.GetTimestamp();
        _jobMessage = message;
        if (_job!.Kind != MachineJobKind.Command || state != JobState.Done)
        {
            var elapsed = Stopwatch.GetElapsedTime(_jobStart, _jobEnd);
            Log.Add(state == JobState.Done ? LogKind.Info : LogKind.Error,
                $"{_job.Name}: {state.ToString().ToLowerInvariant()} after {elapsed:hh\\:mm\\:ss}{(message is null ? string.Empty : $" - {message}")}");
        }
    }

    private void BeginStop()
    {
        if (_linkState != LinkState.Ready)
        {
            return;
        }

        if (_jobState == JobState.Running && _job is not null)
        {
            _jobState = JobState.Stopping;
        }

        _link.Write(stackalloc byte[] { GrblRealtime.FeedHold });
        Log.Add(LogKind.Sent, "! (stop: feed hold, then soft reset)");
        _stopping = true;
        _stopDeadline = Stopwatch.GetTimestamp() + Ticks(_timing.HoldTimeoutMs);
    }

    // The line in flight stays in flight until the welcome line: an ok already on its way arrives
    // before it and is still matched to its line.
    private void SoftReset(string reason)
    {
        if (_linkState != LinkState.Ready)
        {
            return;
        }

        _link.Write(stackalloc byte[] { GrblRealtime.SoftReset });
        Log.Add(LogKind.Sent, "0x18 (soft reset)");
        _stopping = false;
        _awaitingBanner = true;
        _awaitingBannerSince = Stopwatch.GetTimestamp();
        if (IsJobActive)
        {
            FinishJob(JobState.Stopped, _jobState == JobState.Stopping ? StoppedByUserMessage : reason);
        }
    }

    private void Receive(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b == '\n')
            {
                var line = _incoming.ToString().TrimEnd('\r');
                _incoming.Clear();
                if (line.Length > 0)
                {
                    Handle(line);
                }
            }
            else if (_incoming.Length < MaxLineBuffer)
            {
                _incoming.Append((char)b);
            }
        }
    }

    private void Handle(string line)
    {
        if (GrblStatus.IsStatusReport(line))
        {
            OnStatus(line);
        }
        else if (line == "ok")
        {
            OnOk();
        }
        else if (line.StartsWith("error:", StringComparison.Ordinal))
        {
            OnError(line);
        }
        else if (line.StartsWith("ALARM:", StringComparison.Ordinal))
        {
            OnAlarm(line);
        }
        else if (BannerPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
        {
            OnBanner(line);
        }
        else
        {
            Log.Add(LogKind.Received, line);
        }
    }

    private void OnStatus(string line)
    {
        try
        {
            _status = GrblStatus.Parse(line, _status);
        }
        catch (FormatException)
        {
            Log.Add(LogKind.Error, $"Unreadable status report: {line}");
            return;
        }

        _lastStatusAt = Stopwatch.GetTimestamp();
        if (!_identified)
        {
            Identify();
        }

        if (_stopping && _status.IsAtRest)
        {
            SoftReset(StoppedByUserMessage);
        }
    }

    private void OnOk()
    {
        if (_outstanding < 0 || _job is null)
        {
            Log.Add(LogKind.Received, "ok (no line was waiting for it)");
            return;
        }

        var index = _outstanding;
        _outstanding = -1;
        _acknowledged = index + 1;
        _sourceLine = _job.SourceLines[index];
        if (_job.Kind == MachineJobKind.Command)
        {
            Log.Add(LogKind.Received, "ok");
        }

        if (_jobState == JobState.Running && _acknowledged == _job.Count)
        {
            FinishJob(JobState.Done, null);
        }
    }

    private void OnError(string line)
    {
        var text = GrblCodes.DescribeError(line);
        if (_outstanding < 0 || _job is null)
        {
            Log.Add(LogKind.Error, text);
            return;
        }

        var index = _outstanding;
        _outstanding = -1;
        _acknowledged = index + 1;
        var source = _job.SourceLines[index];
        _sourceLine = source;
        var message = source > 0 ? $"Line {source} ({_job.Lines[index]}): {text}" : $"{_job.Lines[index]}: {text}";
        if (_jobState != JobState.Running)
        {
            Log.Add(LogKind.Error, message);
            return;
        }

        FinishJob(JobState.Failed, message);
        switch (_job.Kind)
        {
            case MachineJobKind.Program:
                BeginStop();
                break;
            case MachineJobKind.Check:
                SoftReset(message);
                break;
        }
    }

    private void OnAlarm(string line)
    {
        var text = GrblCodes.DescribeAlarm(line);
        if (IsJobActive)
        {
            _stopping = false;
            FinishJob(JobState.Failed, text);
        }
        else
        {
            Log.Add(LogKind.Error, text);
        }
    }

    private void OnBanner(string line)
    {
        var rest = line[(line.IndexOf(' ') + 1)..];
        var end = rest.IndexOfAny(new[] { ' ', '[' });
        _version = (end < 0 ? rest : rest[..end]).Trim();
        Log.Add(LogKind.Received, line);
        if (!_identified)
        {
            Identify();
            return;
        }

        var expected = _awaitingBanner;
        _awaitingBanner = false;
        if (_outstanding >= 0 && _job is not null && _job.Kind == MachineJobKind.Check && _jobState == JobState.Running && _outstanding == _job.Count - 1)
        {
            // Leaving check mode restarts Grbl; the restart confirms the closing $C.
            OnOk();
            return;
        }

        _outstanding = -1;
        if (!expected && IsJobActive)
        {
            FinishJob(JobState.Failed, UnexpectedResetMessage);
        }
    }

    private void Identify()
    {
        _identified = true;
        _linkState = LinkState.Ready;
        var now = Stopwatch.GetTimestamp();
        _lastStatusAt = now;
        _nextPollAt = now;
        Log.Add(LogKind.Info, $"Connected to {_link.Name}{(_version is null ? string.Empty : $", Grbl {_version}")}");
        _ready.TrySetResult(true);
    }

    private void Publish()
    {
        JobProgress? job = _job is null
            ? null
            : new JobProgress(_job.Kind, _job.Name, _jobState, _job.Count, _sent, _acknowledged, _sourceLine, _jobStart, _jobEnd, _jobMessage);
        var next = new MachineSnapshot(_linkState, _link.Name, _version, _status, job, _outstanding >= 0 || _awaitingBanner, _error);
        if (!next.Equals(_snapshot))
        {
            Volatile.Write(ref _snapshot, next);
        }
    }
}
