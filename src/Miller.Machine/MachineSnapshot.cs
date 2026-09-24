using Miller.Machine.Grbl;

namespace Miller.Machine;

public enum LinkState
{
    // Open, waiting for the controller to identify itself.
    Connecting,

    Ready,

    // Closed by the user or by a failure (Error says which).
    Closed,
}

public enum JobState
{
    Running,

    // Feed hold sent, waiting for the machine to stop before the soft reset.
    Stopping,

    Done,
    Failed,
    Stopped,
}

public sealed record JobProgress(
    MachineJobKind Kind,
    string Name,
    JobState State,
    int Total,
    int Sent,
    int Acknowledged,
    int SourceLine,
    long StartTimestamp,
    long EndTimestamp,
    string? Message)
{
    public bool IsActive => State is JobState.Running or JobState.Stopping;

    public TimeSpan Elapsed => EndTimestamp > 0
        ? System.Diagnostics.Stopwatch.GetElapsedTime(StartTimestamp, EndTimestamp)
        : System.Diagnostics.Stopwatch.GetElapsedTime(StartTimestamp);
}

// What the controller thread knows at one moment; immutable, replaced as a whole.
public sealed record MachineSnapshot(
    LinkState Link,
    string LinkName,
    string? Version,
    GrblStatus Status,
    JobProgress? Job,
    bool LineInFlight,
    string? Error)
{
    public bool IsReady => Link == LinkState.Ready;

    public bool IsJobActive => Job is { IsActive: true };

    // A new job can start: connected, nothing streaming, no line waiting for its answer.
    public bool CanRun => IsReady && !IsJobActive && !LineInFlight;
}
