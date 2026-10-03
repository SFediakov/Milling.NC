using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Simulation;

namespace Miller.Application.Services;

public sealed record SimulationSnapshot(
    Vector3 ToolPosition,
    DirtyRect Dirty,
    IReadOnlyList<SimulationEvent> NewEvents,
    float Progress,
    double ElapsedSimulated,
    int SegmentsCompleted,
    bool Finished);

// Owns engine, clock and collision detection for the loaded pipeline result. The engine cuts a
// clone of the pipeline stock so the result stays intact; Stop swaps in a fresh clone.
public sealed class SimulationService
{
    private readonly SimulationClock _clock = new();
    private readonly List<SimulationEvent> _pending = new();
    private SimulationEngine? _engine;
    private CollisionRecorder? _collisions;

    public PipelineResult? Result { get; private set; }

    // The stock the engine cuts; a new instance after Load and Stop.
    public HeightMap? Stock { get; private set; }

    public bool IsLoaded => _engine is not null;

    public bool IsPlaying => _clock.IsPlaying;

    public bool IsFinished => _engine?.IsFinished ?? false;

    public float Progress => _engine?.Progress ?? 0f;

    public int SegmentsCompleted => _engine?.CurrentSegmentIndex ?? 0;

    public Vector3 ToolPosition => _engine?.ToolPosition ?? Vector3.Zero;

    public double ElapsedSimulated => _clock.ElapsedSimulated;

    public float SpeedFactor
    {
        get => _clock.SpeedFactor;
        set => _clock.SpeedFactor = value;
    }

    public IReadOnlyList<SimulationEvent> Events => _collisions?.Events ?? Array.Empty<SimulationEvent>();

    public void Load(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        _clock.Reset();
        _collisions = new CollisionRecorder(result.Profile, result.Profile.Tool.CutterLength, result.Model, result.Floor);
        ClearEvents();
        Stock = result.Stock.Map.Clone();
        _engine = new SimulationEngine(result.Toolpath, Stock, result.Profile);
        _engine.Sampled += OnSample;
    }

    public void Unload()
    {
        _clock.Reset();
        ClearEvents();
        _engine = null;
        _collisions = null;
        Stock = null;
        Result = null;
    }

    public void Play()
    {
        if (IsLoaded && !IsFinished)
        {
            _clock.Play();
        }
    }

    public void Pause() => _clock.Pause();

    public void Stop()
    {
        var engine = Require();
        _clock.Reset();
        Replay(engine);
    }

    // Layers of the loaded toolpath (PipelineResult.Layers).
    public int LayerCount => Result?.Layers.Count ?? 0;

    // The layer the next segment to cover belongs to (the last layer whose first segment is at or before
    // it), the last layer once finished, -1 without layers.
    public int LayerIndex
    {
        get
        {
            if (_engine is null || LayerCount == 0)
            {
                return -1;
            }

            var layers = Result!.Layers;
            var segment = _engine.CurrentSegmentIndex;
            var index = 0;
            while (index + 1 < layers.Count && layers[index + 1].FirstSegment <= segment)
            {
                index++;
            }

            return index;
        }
    }

    // Layers whose every segment is covered: the layer index while the tool is inside or at the start of
    // that layer, all of them once finished.
    public int CompletedLayers => IsFinished ? LayerCount : Math.Max(LayerIndex, 0);

    // Moves to the end of the current layer: the start of the next one, the end of the path on the last.
    public SimulationSnapshot SeekToNextLayer()
    {
        Require();
        var layers = Result!.Layers;
        var next = LayerIndex + 1;
        return SeekToSegment(next < layers.Count ? layers[next].FirstSegment : Result.Toolpath.Count);
    }

    // Moves to the start of the current layer, or of the previous one when the tool stands at its start.
    public SimulationSnapshot SeekToPreviousLayer()
    {
        var engine = Require();
        var layers = Result!.Layers;
        var layer = LayerIndex;
        if (layer < 0)
        {
            return SeekToSegment(0);
        }

        var start = layers[layer].FirstSegment;
        var atStart = engine.CurrentSegmentIndex == start && engine.AtSegmentStart;
        return SeekToSegment(atStart && layer > 0 ? layers[layer - 1].FirstSegment : start);
    }

    // Moves the simulation to the start of a segment (the segment count for the end) with the rules of
    // SeekTo: forward sweeps in place, backward replays from a fresh stock.
    public SimulationSnapshot SeekToSegment(int index)
    {
        var engine = Require();
        _clock.Pause();
        var target = Math.Clamp(index, 0, Result!.Toolpath.Count);
        if (target < engine.CurrentSegmentIndex || (target == engine.CurrentSegmentIndex && !engine.AtSegmentStart))
        {
            Replay(engine);
        }

        var result = engine.SeekToSegment(target);
        _clock.Seek(engine.ElapsedSeconds);
        return Snapshot(result);
    }

    public SimulationSnapshot StepOnce(double simSeconds) => Snapshot(Require().Step(simSeconds));

    // Moves the simulation to a fraction of the path length. Forward from the current position the
    // engine sweeps the part in between; backward it replays from a fresh stock, so events and the
    // stock match a run that stopped there. The stock instance is new after a backward seek. Pauses
    // first so a UI timer tick cannot step the engine while this runs on another thread.
    public SimulationSnapshot SeekTo(float fraction)
    {
        var engine = Require();
        _clock.Pause();
        var target = Math.Clamp(fraction, 0f, 1f) * engine.TotalLength;
        if (target < engine.CoveredLength)
        {
            Replay(engine);
        }

        var result = engine.SeekTo(target);
        _clock.Seek(engine.ElapsedSeconds);
        return Snapshot(result);
    }

    // A fresh stock clone for the engine; the events of the run so far are dropped with it.
    private void Replay(SimulationEngine engine)
    {
        ClearEvents();
        Stock = Result!.Stock.Map.Clone();
        engine.Reset(Stock);
    }

    // Pauses first so a UI timer tick cannot step the engine while this runs on another thread; the
    // clock takes the engine's elapsed time afterwards, like a seek, so the readout shows the total.
    public SimulationSnapshot RunToEnd()
    {
        var engine = Require();
        _clock.Pause();
        var result = engine.RunToEnd();
        _clock.Seek(engine.ElapsedSeconds);
        return Snapshot(result);
    }

    // Real seconds since the last call; nothing moves while paused or before Load.
    public SimulationSnapshot Advance(double realSeconds)
    {
        var engine = Require();
        var simulated = _clock.Advance(realSeconds);
        if (!(simulated > 0))
        {
            return Snapshot(new StepResult(engine.ToolPosition, DirtyRect.Empty, engine.CurrentSegmentIndex, engine.IsFinished));
        }

        var result = engine.Step(simulated);
        if (result.Finished)
        {
            _clock.Pause();
        }

        return Snapshot(result);
    }

    private SimulationEngine Require() => _engine ?? throw new InvalidOperationException("Load a pipeline result before simulating.");

    private SimulationSnapshot Snapshot(StepResult result)
    {
        var fresh = _pending.ToArray();
        _pending.Clear();
        return new SimulationSnapshot(result.ToolPosition, result.Dirty, fresh, Progress, _clock.ElapsedSimulated, result.SegmentsCompleted, result.Finished);
    }

    private void ClearEvents()
    {
        _collisions?.Clear();
        _pending.Clear();
    }

    // One event per segment and kind keeps the list readable for a rapid crossing many cells.
    private void OnSample(SimulationSample sample)
    {
        if (_collisions!.Record(Stock!, sample) is { } found)
        {
            _pending.Add(found);
        }
    }
}
