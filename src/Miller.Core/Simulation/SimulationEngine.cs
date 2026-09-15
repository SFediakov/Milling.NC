using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

public sealed record StepResult(Vector3 ToolPosition, DirtyRect Dirty, int SegmentsCompleted, bool Finished);

// A tool position the engine visited: the end of every covered part, and points along rapids at
// RapidSampleSpacing so a listener can check them against the stock.
public readonly record struct SimulationSample(int SegmentIndex, MoveKind Kind, Vector3 Tip);

// Walks the toolpath by time: each segment is covered at its own rate (mm/min), feed and plunge
// parts sweep the stock, rapids remove nothing. Steps compose: covering a segment in parts gives
// the same stock as covering it at once because every sweep samples all cells on its path.
public sealed class SimulationEngine
{
    private readonly Toolpath _toolpath;
    private readonly ToolProfile _profile;
    private readonly float _totalLength;
    private int _index;
    private float _covered;
    private float _doneLength;

    public SimulationEngine(Toolpath toolpath, HeightMap stock, ToolProfile profile)
    {
        _toolpath = toolpath ?? throw new ArgumentNullException(nameof(toolpath));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Stock = stock ?? throw new ArgumentNullException(nameof(stock));
        if (profile.CellSize != stock.CellSize)
        {
            throw new ArgumentException($"Profile cell size {profile.CellSize} differs from the stock cell size {stock.CellSize}.", nameof(profile));
        }

        foreach (var s in toolpath.Segments)
        {
            _totalLength += s.Length;
        }

        ToolPosition = StartPosition();
    }

    public event Action<SimulationSample>? Sampled;

    public HeightMap Stock { get; private set; }

    public Vector3 ToolPosition { get; private set; }

    public int CurrentSegmentIndex => _index;

    public bool IsFinished => _index >= _toolpath.Count;

    public float Progress => _totalLength > 0 ? Math.Clamp((_doneLength + _covered) / _totalLength, 0f, 1f) : (IsFinished ? 1f : 0f);

    // Rapids are checked at least once per cutter radius, and never coarser than a cell.
    public float RapidSampleSpacing => MathF.Max(_profile.CellSize, _profile.Tool.CutterRadius);

    public StepResult Step(double simSeconds)
    {
        var dirty = DirtyRect.Empty;
        var remaining = simSeconds;
        while (remaining > 0 && !IsFinished)
        {
            var segment = _toolpath.Segments[_index];
            var length = segment.Length;
            var left = length - _covered;
            var rate = segment.FeedRate / 60f;
            var timeToFinish = left / rate;
            if (timeToFinish <= remaining)
            {
                dirty = dirty.Union(Cover(segment, _covered, length));
                remaining -= timeToFinish;
                _doneLength += length;
                _index++;
                _covered = 0;
            }
            else
            {
                var to = _covered + (float)(rate * remaining);
                dirty = dirty.Union(Cover(segment, _covered, to));
                _covered = to;
                remaining = 0;
            }
        }

        return new StepResult(ToolPosition, dirty, _index, IsFinished);
    }

    public StepResult RunToEnd() => Step(double.PositiveInfinity);

    // Whole toolpath, checking the token once per segment.
    public StepResult RunToEnd(CancellationToken cancellation)
    {
        var dirty = DirtyRect.Empty;
        var result = new StepResult(ToolPosition, dirty, _index, IsFinished);
        while (!IsFinished)
        {
            cancellation.ThrowIfCancellationRequested();
            var segment = _toolpath.Segments[_index];
            var seconds = (segment.Length - _covered) / (segment.FeedRate / 60f);
            result = Step(seconds > 0 ? seconds : 1e-6);
            dirty = dirty.Union(result.Dirty);
        }

        return result with { Dirty = dirty };
    }

    public void Reset(HeightMap freshStock)
    {
        ArgumentNullException.ThrowIfNull(freshStock);
        if (freshStock.CellSize != _profile.CellSize)
        {
            throw new ArgumentException("Fresh stock must use the profile cell size.", nameof(freshStock));
        }

        Stock = freshStock;
        _index = 0;
        _covered = 0;
        _doneLength = 0;
        ToolPosition = StartPosition();
    }

    private Vector3 StartPosition() => _toolpath.Count > 0 ? _toolpath.Segments[0].Start : Vector3.Zero;

    private DirtyRect Cover(ToolpathSegment segment, float fromDistance, float toDistance)
    {
        var from = PointAt(segment, fromDistance);
        var to = PointAt(segment, toDistance);
        ToolPosition = to;
        if (segment.Kind == MoveKind.Rapid)
        {
            SampleRapid(segment, fromDistance, toDistance);
            return DirtyRect.Empty;
        }

        var dirty = MaterialRemover.Sweep(Stock, _profile, from, to);
        Sampled?.Invoke(new SimulationSample(_index, segment.Kind, to));
        return dirty;
    }

    private void SampleRapid(ToolpathSegment segment, float fromDistance, float toDistance)
    {
        if (Sampled is null)
        {
            return;
        }

        if (fromDistance == 0)
        {
            Sampled(new SimulationSample(_index, MoveKind.Rapid, segment.Start));
        }

        var spacing = RapidSampleSpacing;
        for (var d = fromDistance + spacing; d < toDistance; d += spacing)
        {
            Sampled(new SimulationSample(_index, MoveKind.Rapid, PointAt(segment, d)));
        }

        Sampled(new SimulationSample(_index, MoveKind.Rapid, PointAt(segment, toDistance)));
    }

    private static Vector3 PointAt(ToolpathSegment segment, float distance)
    {
        var length = segment.Length;
        if (length <= 0)
        {
            return segment.Start;
        }

        return Vector3.Lerp(segment.Start, segment.End, Math.Clamp(distance / length, 0f, 1f));
    }
}
