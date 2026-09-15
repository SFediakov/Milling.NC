namespace Miller.Core.Simulation;

// Real time to simulated time with a speed factor clamped to [0.1, 1000].
public sealed class SimulationClock
{
    public const float MinSpeedFactor = 0.1f;
    public const float MaxSpeedFactor = 1000f;
    public const float DefaultSpeedFactor = 1f;

    private float _speedFactor = DefaultSpeedFactor;

    public float SpeedFactor
    {
        get => _speedFactor;
        set => _speedFactor = float.IsNaN(value) ? MinSpeedFactor : Math.Clamp(value, MinSpeedFactor, MaxSpeedFactor);
    }

    public bool IsPlaying { get; private set; }

    public double ElapsedSimulated { get; private set; }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Reset()
    {
        IsPlaying = false;
        ElapsedSimulated = 0;
    }

    // Simulated seconds covered by realSeconds of wall time; 0 while paused or for a negative input.
    public double Advance(double realSeconds)
    {
        if (!IsPlaying || !(realSeconds > 0))
        {
            return 0;
        }

        var simulated = realSeconds * _speedFactor;
        ElapsedSimulated += simulated;
        return simulated;
    }
}
