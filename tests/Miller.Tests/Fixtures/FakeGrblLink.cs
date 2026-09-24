using System.Diagnostics;
using System.Text;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;

namespace Miller.Tests.Fixtures;

// An in-memory Grbl 1.1 for the controller tests. Realtime bytes act at once (status report, hold,
// resume, reset); every line is answered by Respond (ok by default) after OkDelay, and the answer
// only becomes readable then. A sender that writes a line before reading the answer to the previous
// one therefore shows up in MaxInFlight. A hold reports Hold:1 once, then Hold:0.
public sealed class FakeGrblLink : IMachineLink
{
    public const string Banner = "Grbl 1.1h ['$' for help]";

    private readonly object _gate = new();
    private readonly List<(long Due, byte[] Bytes, bool IsAnswer)> _output = new();
    private readonly StringBuilder _line = new();
    private bool _failed;
    private bool _disposed;
    private int _inFlight;
    private bool _holdReported;

    public FakeGrblLink(bool banner = true)
    {
        if (banner)
        {
            Emit(Banner);
        }
    }

    public string Name => "fake";

    public TimeSpan OkDelay { get; set; } = TimeSpan.Zero;

    // The answer to one received line; null means no answer at all.
    public Func<string, string?> Respond { get; set; } = _ => "ok";

    public bool AnswerStatus { get; set; } = true;

    // A hold that keeps reporting Hold:1, as a machine that cannot stop would.
    public bool HoldNeverCompletes { get; set; }

    public bool BannerAfterReset { get; set; } = true;

    public string State { get; set; } = GrblStatus.Idle;

    public List<string> Lines { get; } = new();

    public List<byte> RealtimeBytes { get; } = new();

    public int MaxInFlight { get; private set; }

    public int LinesAtFirstHold { get; private set; } = -1;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    public void Emit(string line) => Enqueue(Stopwatch.GetTimestamp(), line, false);

    // The next read throws, as a pulled cable does.
    public void Fail()
    {
        lock (_gate)
        {
            _failed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var deadline = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        lock (_gate)
        {
            while (true)
            {
                if (_failed)
                {
                    throw new MachineLinkException("fake: the port was removed.");
                }

                var now = Stopwatch.GetTimestamp();
                if (_output.Count > 0 && _output[0].Due <= now)
                {
                    // Like a stream: at most the buffer, the rest stays first in line.
                    var (due, bytes, isAnswer) = _output[0];
                    var count = Math.Min(bytes.Length, buffer.Length);
                    bytes.AsSpan(0, count).CopyTo(buffer);
                    if (count < bytes.Length)
                    {
                        _output[0] = (due, bytes[count..], isAnswer);
                        return count;
                    }

                    _output.RemoveAt(0);
                    if (isAnswer)
                    {
                        _inFlight--;
                    }

                    return count;
                }

                var wait = _output.Count > 0 ? Math.Min(_output[0].Due, deadline) - now : deadline - now;
                if (wait <= 0)
                {
                    return 0;
                }

                Monitor.Wait(_gate, TimeSpan.FromTicks(Math.Max(1, wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == '\n')
            {
                OnLine(_line.ToString());
                _line.Clear();
            }
            else if (b == GrblRealtime.StatusQuery || b == GrblRealtime.SoftReset || b >= 0x80 || b == GrblRealtime.FeedHold || b == GrblRealtime.CycleStart)
            {
                OnRealtime(b);
            }
            else
            {
                _line.Append((char)b);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void OnRealtime(byte b)
    {
        lock (_gate)
        {
            if (b != GrblRealtime.StatusQuery)
            {
                RealtimeBytes.Add(b);
            }
        }

        switch (b)
        {
            case GrblRealtime.StatusQuery:
                if (AnswerStatus)
                {
                    var state = State;
                    if (state == GrblStatus.Hold)
                    {
                        state = _holdReported && !HoldNeverCompletes ? "Hold:0" : "Hold:1";
                        _holdReported = true;
                    }

                    Emit($"<{state}|MPos:1.000,2.000,3.000|FS:0,0|WCO:0.500,0.500,0.500>");
                }

                break;
            case GrblRealtime.FeedHold:
                if (LinesAtFirstHold < 0)
                {
                    LinesAtFirstHold = Lines.Count;
                }

                State = GrblStatus.Hold;
                _holdReported = false;
                break;
            case GrblRealtime.CycleStart:
                State = GrblStatus.Idle;
                break;
            case GrblRealtime.SoftReset:
                lock (_gate)
                {
                    _output.RemoveAll(o => o.IsAnswer);
                    _inFlight = 0;
                }

                State = GrblStatus.Idle;
                if (BannerAfterReset)
                {
                    Emit(Banner);
                }

                break;
        }
    }

    private void OnLine(string line)
    {
        lock (_gate)
        {
            Lines.Add(line);
        }

        if (line == MachineJob.CheckModeLine)
        {
            var leaving = State == GrblStatus.Check;
            State = leaving ? GrblStatus.Idle : GrblStatus.Check;
            Emit(leaving ? "[MSG:Disabled]" : "[MSG:Enabled]");
            Answer("ok");
            if (leaving)
            {
                Enqueue(Due(), Banner, false);
            }

            return;
        }

        var answer = Respond(line);
        if (answer is not null)
        {
            Answer(answer);
        }
    }

    private void Answer(string text)
    {
        lock (_gate)
        {
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
        }

        Enqueue(Due(), text, true);
    }

    private long Due() => Stopwatch.GetTimestamp() + (long)(OkDelay.TotalSeconds * Stopwatch.Frequency);

    private void Enqueue(long due, string line, bool isAnswer)
    {
        lock (_gate)
        {
            var index = _output.FindLastIndex(o => o.Due <= due) + 1;
            _output.Insert(index, (due, Encoding.ASCII.GetBytes(line + "\r\n"), isAnswer));
            Monitor.PulseAll(_gate);
        }
    }
}
