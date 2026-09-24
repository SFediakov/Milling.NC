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
        ClearEvents();
        Stock = Result!.Stock.Map.Clone();
        engine.Reset(Stock);
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
            ClearEvents();
            Stock = Result!.Stock.Map.Clone();
            engine.Reset(Stock);
        }

        var result = engine.SeekTo(target);
        _clock.Seek(engine.ElapsedSeconds);
        return Snapshot(result);
    }

    // Pauses first so a UI timer tick cannot step the engine while this runs on another thread.
    public SimulationSnapshot RunToEnd()
    {
        var engine = Require();
        _clock.Pause();
        return Snapshot(engine.RunToEnd());
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
