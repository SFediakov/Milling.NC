using System.Numerics;
using Miller.Machine;

namespace Miller.Application.Services;

// The move of a running program the tool is on (T-205). Grbl acknowledges a line once it is in its
// planner, which holds up to about 15 moves the tool has not reached, so the acknowledged count runs
// ahead of the machine. The move is found from the reported work position among the segments between
// the one found last and the end of the acknowledged lines: the earliest whose distance is within
// MatchTolerance of the nearest, so a point the path passes twice in that window counts at its first
// pass. It never moves back during a run; a new run starts again from the first segment.
public sealed class ExecutionTracker
{
    // Grbl reports positions to 0.001 mm and the tool stands within a step of its path.
    public const float MatchTolerance = 0.01f;

    private ProgramPath? _path;
    private long _run = -1;
    private int _executing;

    // Segments before the result are done, the result is the one the tool is on (the segment count once
    // the run is done). Without a position (not connected) the last result stands.
    public int Executing(ProgramPath path, JobProgress? job, Vector3? position)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (job is null)
        {
            return 0;
        }

        if (!ReferenceEquals(path, _path) || job.StartTimestamp != _run)
        {
            _path = path;
            _run = job.StartTimestamp;
            _executing = 0;
        }

        if (job.State == JobState.Done)
        {
            _executing = path.Segments.Count;
            return _executing;
        }

        var end = path.SegmentsDone(job.Acknowledged);
        if (position is not Vector3 at || end <= _executing)
        {
            return _executing;
        }

        var segments = path.Segments;
        var nearest = float.MaxValue;
        for (var k = _executing; k < end; k++)
        {
            nearest = MathF.Min(nearest, Distance(at, segments[k].Start, segments[k].End));
        }

        for (var k = _executing; k < end; k++)
        {
            if (Distance(at, segments[k].Start, segments[k].End) <= nearest + MatchTolerance)
            {
                _executing = k;
                break;
            }
        }

        return _executing;
    }

    private static float Distance(Vector3 point, Vector3 start, Vector3 end)
    {
        var along = end - start;
        var squared = along.LengthSquared();
        var t = squared > 0 ? Math.Clamp(Vector3.Dot(point - start, along) / squared, 0f, 1f) : 0f;
        return Vector3.Distance(point, start + along * t);
    }
}
