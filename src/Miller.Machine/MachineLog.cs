namespace Miller.Machine;

public enum LogKind
{
    Sent,
    Received,
    Info,
    Error,
}

public sealed record LogEntry(long Sequence, LogKind Kind, string Text);

// The console: the last entries of the traffic worth reading (not the ok of every program line and
// not the status reports), numbered so a reader asks only for what is new.
public sealed class MachineLog
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _next = 1;

    public MachineLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public void Add(LogKind kind, string text)
    {
        lock (_gate)
        {
            _entries.Enqueue(new LogEntry(_next++, kind, text));
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogEntry> Since(long sequence)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Sequence > sequence).ToList();
        }
    }
}
