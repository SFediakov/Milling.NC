using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

public sealed record StepResult(Vector3 ToolPosition, DirtyRect Dirty, int SegmentsCompleted, bool Finished);

// A tool position the engine visited for a collision check: on feeds and plunges the sweep samples
// at least SampleSpacing apart and the segment end, on rapids the start, every SampleSpacing along
// and the end. The positions belong to the segment, not to the parts a playback covers it in, so a
// listener sees the same positions at any speed (T-161).
public readonly record struct SimulationSample(int SegmentIndex, MoveKind Kind, Vector3 Tip);

// Walks the toolpath by time or by distance: each segment is covered at its own rate (mm/min),
// feed and plunge parts sweep the stock, rapids remove nothing. Steps compose: covering a segment
// in parts gives the same stock and the same samples as covering it at once, because the sweep
// stamps the samples of the whole segment (cell / 2 apart) and reports the positions the
// generation's collision check uses. SeekTo covers forward only; a position behind the current
// one needs Reset first.
public sealed class SimulationEngine
{
    private readonly Toolpath _toolpath;
    private readonly ToolProfile _profile;
    private readonly float _totalLength;
    private int _index;
    private float _covered;
    private float _doneLength;
    private double _doneSeconds;

    // The sampling of the current segment: its sweep sample count and length, the next sample to
    // stamp, the sweep length since the last head check, and the next rapid check distance.
    private bool _planned;
    private int _steps;
    private float _length;
    private int _nextSample;
    private float _sinceCheck;
    private float _nextRapid;

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

    // Nothing of the current segment is covered yet (also true at the end of the path).
    public bool AtSegmentStart => _covered == 0;

    public bool IsFinished => _index >= _toolpath.Count;

    public float Progress => _totalLength > 0 ? Math.Clamp((_doneLength + _covered) / _totalLength, 0f, 1f) : (IsFinished ? 1f : 0f);

    public float TotalLength => _totalLength;

    // Path length behind the tool, in mm.
    public float CoveredLength => _doneLength + _covered;

    // Simulated seconds the covered path takes at the segment rates.
    public double ElapsedSeconds => _doneSeconds + (IsFinished ? 0 : _covered / (_toolpath.Segments[_index].FeedRate / 60f));

    // The head is checked at least once per cutter radius along every segment, and never coarser than
    // a cell: the spacing of the generation's collision check.
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
                Complete(segment);
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

    // Covers the path up to the given length from the start (clamped to the total); the position
    // must not lie behind the tool.
    public StepResult SeekTo(float length)
    {
        var target = Math.Clamp(length, 0f, _totalLength);
        if (target < CoveredLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, $"Seek target lies behind the covered length {CoveredLength}; reset the engine first.");
        }

        var dirty = DirtyRect.Empty;
        while (!IsFinished)
        {
            var segment = _toolpath.Segments[_index];
            var end = _doneLength + segment.Length;
            if (end <= target)
            {
                dirty = dirty.Union(Cover(segment, _covered, segment.Length));
                Complete(segment);
                continue;
            }

            var to = target - _doneLength;
            if (to > _covered)
            {
                dirty = dirty.Union(Cover(segment, _covered, to));
                _covered = to;
            }

            break;
        }

        return new StepResult(ToolPosition, dirty, _index, IsFinished);
    }

    // Covers whole segments up to the start of the segment at `index` (the segment count for the end);
    // the target must not lie behind the tool.
    public StepResult SeekToSegment(int index)
    {
        var target = Math.Clamp(index, 0, _toolpath.Count);
        if (target < _index || (target == _index && _covered > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Segment {index} lies behind the tool in segment {_index}; reset the engine first.");
        }

        var dirty = DirtyRect.Empty;
        while (_index < target)
        {
            var segment = _toolpath.Segments[_index];
            dirty = dirty.Union(Cover(segment, _covered, segment.Length));
            Complete(segment);
        }

        return new StepResult(ToolPosition, dirty, _index, IsFinished);
    }

    // Whole toolpath, checking the token and reporting Progress once per segment.
    public StepResult RunToEnd(CancellationToken cancellation, IProgress<float>? progress = null)
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
            progress?.Report(Progress);
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
        _doneSeconds = 0;
        _planned = false;
        ToolPosition = StartPosition();
    }

    private void Complete(ToolpathSegment segment)
    {
        _doneLength += segment.Length;
        _doneSeconds += segment.Length / (segment.FeedRate / 60f);
        _index++;
        _covered = 0;
        _planned = false;
    }

    private Vector3 StartPosition() => _toolpath.Count > 0 ? _toolpath.Segments[0].Start : Vector3.Zero;

    private void Plan(ToolpathSegment segment)
    {
        _steps = MaterialRemover.SampleCount(Stock, segment.Start, segment.End, out _length);
        _nextSample = 0;
        _sinceCheck = 0;
        _nextRapid = RapidSampleSpacing;
        _planned = true;
    }

    // The part [fromDistance, toDistance] of the segment: the stamps and the checks are the segment's
    // own samples that fall into the part, the tool position the part's end.
    private DirtyRect Cover(ToolpathSegment segment, float fromDistance, float toDistance)
    {
        if (!_planned)
        {
            Plan(segment);
        }

        ToolPosition = PointAt(segment, toDistance);
        if (segment.Kind == MoveKind.Rapid)
        {
            SampleRapid(segment, fromDistance, toDistance);
            return DirtyRect.Empty;
        }

        var atEnd = toDistance >= _length;
        var last = atEnd || _length <= 0 ? _steps : Math.Min(_steps, (int)MathF.Floor(toDistance / _length * _steps));
        var spacing = RapidSampleSpacing;
        var increment = _steps == 0 ? 0f : _length / _steps;
        var dirty = DirtyRect.Empty;
        var first = _nextSample;
        for (var n = _nextSample; n <= last; n++)
        {
            _sinceCheck += increment;
            if (n == _steps || _sinceCheck >= spacing)
            {
                dirty = dirty.Union(MaterialRemover.SweepSamples(Stock, _profile, segment.Start, segment.End, first, n, out var tip));
                Sampled?.Invoke(new SimulationSample(_index, segment.Kind, tip));
                _sinceCheck = 0;
                first = n + 1;
            }
        }

        if (first <= last)
        {
            dirty = dirty.Union(MaterialRemover.SweepSamples(Stock, _profile, segment.Start, segment.End, first, last, out _));
        }

        _nextSample = Math.Max(_nextSample, last + 1);
        return dirty;
    }

    // Rapids: the start, every RapidSampleSpacing along the segment and the end; each position once,
    // in the part that reaches it.
    private void SampleRapid(ToolpathSegment segment, float fromDistance, float toDistance)
    {
        if (_nextSample == 0)
        {
            Sampled?.Invoke(new SimulationSample(_index, MoveKind.Rapid, segment.Start));
            _nextSample = 1;
        }

        while (_nextRapid < _length && _nextRapid <= toDistance)
        {
            Sampled?.Invoke(new SimulationSample(_index, MoveKind.Rapid, TipAt(segment, _nextRapid)));
            _nextRapid += RapidSampleSpacing;
        }

        if (toDistance >= _length && _nextSample == 1)
        {
            Sampled?.Invoke(new SimulationSample(_index, MoveKind.Rapid, segment.End));
            _nextSample = 2;
        }
    }

    // The point at a distance along the segment, by the segment's native length (the check's rule).
    private Vector3 TipAt(ToolpathSegment segment, float distance)
        => _length <= 0 ? segment.Start : Vector3.Lerp(segment.Start, segment.End, Math.Clamp(distance / _length, 0f, 1f));

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
