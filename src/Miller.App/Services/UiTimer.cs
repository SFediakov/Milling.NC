using System.Diagnostics;
using Avalonia.Threading;
using Miller.App.ViewModels;
using Miller.Application.Services;

namespace Miller.App.Services;

// 60 Hz dispatcher timer: measures the real time since the previous tick with a stopwatch, advances
// the simulation by it and hands the snapshot to the viewport. Ticks while the service is not
// playing only restart the stopwatch, so play never starts with a large first step.
public sealed class UiTimer : IDisposable
{
    public const int FramesPerSecond = 60;

    private readonly SimulationService _simulation;
    private readonly ViewportViewModel _viewport;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _watch = new();

    public UiTimer(SimulationService simulation, ViewportViewModel viewport)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / FramesPerSecond), DispatcherPriority.Render, OnTick);
        _timer.Stop();
    }

    // Raised after every snapshot that moved the simulation; the simulation panel follows it.
    public event EventHandler<SimulationSnapshot>? Ticked;

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        _watch.Restart();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    // One frame worth of simulation from a measured real duration; public so tests drive it.
    public void Tick(double realSeconds)
    {
        if (!_simulation.IsPlaying)
        {
            return;
        }

        var snapshot = _simulation.Advance(realSeconds);
        _viewport.ApplySimulation(snapshot);
        Ticked?.Invoke(this, snapshot);
    }

    public void Dispose() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var elapsed = _watch.Elapsed.TotalSeconds;
        _watch.Restart();
        Tick(elapsed);
    }
}
