using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;

namespace Miller.App.ViewModels;

// Play, pause, stop, step, run to end and seek (a press on the progress bar) with enable rules per
// state, the speed factor shared by slider and text box and persisted in the settings, progress,
// simulated time and the collision events. The menu binds to the same commands, so the rules exist
// once.
public sealed partial class SimulationViewModel : ViewModelBase
{
    public const double StepSeconds = 1.0;
    public const string ReadyStatus = "Simulation ready";
    public const string PlayingStatus = "Simulation playing";
    public const string PausedStatus = "Simulation paused";
    public const string StoppedStatus = "Simulation stopped";
    public const string FinishedStatus = "Simulation finished";
    public const string RunningStatus = "Simulating the whole toolpath";
    public const string SeekingStatus = "Moving the simulation";
    public const string NotLoadedText = "Generate a toolpath to simulate it.";
    public const string NoLayersText = "No layers";
    public const string SpeedFormat = "0.###";
    public const string SpeedInvalidMessage = "Speed must be a number between 0.1 and 1000.";

    private readonly SimulationService _simulation;
    private readonly SettingsService _settings;
    private readonly ViewportViewModel _viewport;
    private bool _finishAnnounced;

    [ObservableProperty]
    private float _progress;

    [ObservableProperty]
    private string _elapsedText = FormatSeconds(0);

    [ObservableProperty]
    private int _collisionCount;

    [ObservableProperty]
    private string _lastEventText = string.Empty;

    [ObservableProperty]
    private bool _isRunningToEnd;

    [ObservableProperty]
    private bool _isSeeking;

    [ObservableProperty]
    private string? _speedError;

    public SimulationViewModel(SimulationService simulation, SettingsService settings, ViewportViewModel viewport)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _simulation.SpeedFactor = settings.SpeedFactor;
    }

    public event EventHandler<string>? StatusChanged;

    // Play started: the analysis view returns to the stock so the frames show the cut.
    public event EventHandler? PlaybackStarted;

    public bool IsLoaded => _simulation.IsLoaded;

    public bool IsPlaying => _simulation.IsPlaying;

    public bool IsFinished => _simulation.IsFinished;

    public int LayerCount => _simulation.LayerCount;

    // Layers done and the layer the tool works in, from the service state.
    public string LayerText
    {
        get
        {
            var count = LayerCount;
            if (!IsLoaded || count == 0)
            {
                return NoLayersText;
            }

            var done = _simulation.CompletedLayers;
            if (IsFinished)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{done} of {count} layers done");
            }

            var layer = _simulation.LayerIndex;
            return string.Create(CultureInfo.InvariantCulture, $"{done} of {count} layers done, layer {layer + 1} at Z {_simulation.Result!.Layers[layer].Level:0.###}");
        }
    }

    // A sweep on another thread is in progress (run to end or seek): generation and the other
    // simulation commands wait for it.
    public bool IsWorking => IsRunningToEnd || IsSeeking;

    public string StateText => !IsLoaded ? NotLoadedText
        : IsRunningToEnd ? RunningStatus
        : IsSeeking ? SeekingStatus
        : IsFinished ? FinishedStatus
        : IsPlaying ? PlayingStatus
        : Progress > 0 ? PausedStatus : ReadyStatus;

    // Clamped by the service; the setting keeps the clamped value.
    public float SpeedFactor
    {
        get => _simulation.SpeedFactor;
        set
        {
            _simulation.SpeedFactor = value;
            _settings.SpeedFactor = _simulation.SpeedFactor;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText
    {
        get => SpeedFactor.ToString(SpeedFormat, CultureInfo.InvariantCulture);
        set
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !float.IsFinite(parsed))
            {
                SpeedError = SpeedInvalidMessage;
                return;
            }

            SpeedFactor = parsed;
            SpeedError = parsed == SpeedFactor
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"Speed clamped to {SpeedText} (allowed 0.1 to 1000).");
        }
    }

    private bool CanPlay => IsLoaded && !IsPlaying && !IsFinished && !IsWorking;

    private bool CanPause => IsPlaying && !IsWorking;

    private bool CanStop => IsLoaded && !IsWorking;

    private bool CanSeek => IsLoaded && !IsWorking;

    private bool CanNextLayer => CanSeek && !IsFinished && LayerCount > 0;

    private bool CanPreviousLayer => CanSeek && LayerCount > 0 && _simulation.Progress > 0;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        _simulation.Play();
        _finishAnnounced = false;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
        Announce(PlayingStatus);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _simulation.Pause();
        Announce(PausedStatus);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _simulation.Stop();
        ShowStock();
        ResetReadout();
        Announce(StoppedStatus);
        Refresh();
    }

    // One simulated second, applied to the viewport at once.
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Step()
    {
        var snapshot = _simulation.StepOnce(StepSeconds);
        _viewport.ApplySimulation(snapshot);
        Apply(snapshot);
    }

    // The whole toolpath takes seconds to sweep, so it runs off the UI thread; generation and the
    // other simulation commands stay disabled meanwhile.
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task RunToEndAsync()
    {
        IsRunningToEnd = true;
        Announce(RunningStatus);
        Refresh();
        try
        {
            var snapshot = await Task.Run(_simulation.RunToEnd);
            ShowStock();
            Apply(snapshot);
        }
        finally
        {
            IsRunningToEnd = false;
            Refresh();
        }
    }

    // A press on the progress bar at a fraction of its width.
    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task SeekAsync(float fraction) => SeekWithAsync(() => _simulation.SeekTo(fraction));

    // Layer by layer: to the end of the current layer, or back to the start of the current one and
    // then of the one before.
    [RelayCommand(CanExecute = nameof(CanNextLayer))]
    private Task NextLayerAsync() => SeekWithAsync(_simulation.SeekToNextLayer);

    [RelayCommand(CanExecute = nameof(CanPreviousLayer))]
    private Task PreviousLayerAsync() => SeekWithAsync(_simulation.SeekToPreviousLayer);

    // Every seek: the sweep runs off the UI thread like run to end; a simulation that was playing
    // continues from the new position unless it is the end.
    private async Task SeekWithAsync(Func<SimulationSnapshot> seek)
    {
        var resume = IsPlaying;
        _simulation.Pause();
        IsSeeking = true;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
        Announce(SeekingStatus);
        Refresh();
        try
        {
            var snapshot = await Task.Run(seek);
            ShowStock();
            _finishAnnounced = false;
            Apply(snapshot);
            if (resume && !snapshot.Finished)
            {
                _simulation.Play();
                Announce(PlayingStatus);
            }
            else if (!snapshot.Finished)
            {
                Announce(PausedStatus);
            }
        }
        finally
        {
            IsSeeking = false;
            Refresh();
        }
    }

    // Space in the viewport.
    public void TogglePlayPause()
    {
        if (PauseCommand.CanExecute(null))
        {
            Pause();
        }
        else if (PlayCommand.CanExecute(null))
        {
            Play();
        }
    }

    // Every snapshot the UI timer, Step or RunToEnd produced.
    public void Apply(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Progress = snapshot.Progress;
        ElapsedText = FormatSeconds(snapshot.ElapsedSimulated);
        CollisionCount = _simulation.Events.Count;
        LastEventText = _simulation.Events.Count > 0 ? _simulation.Events[^1].Message : string.Empty;
        if (snapshot.Finished && !_finishAnnounced)
        {
            _finishAnnounced = true;
            Announce(string.Create(CultureInfo.InvariantCulture, $"{FinishedStatus}, {CollisionCount} events"));
        }

        Refresh();
    }

    // After the main view model loaded or unloaded a pipeline result.
    public void OnLoadedChanged()
    {
        if (IsLoaded)
        {
            ShowStock();
        }

        ResetReadout();
        Refresh();
    }

    public void Refresh()
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        RunToEndCommand.NotifyCanExecuteChanged();
        SeekCommand.NotifyCanExecuteChanged();
        NextLayerCommand.NotifyCanExecuteChanged();
        PreviousLayerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(LayerCount));
        OnPropertyChanged(nameof(LayerText));
    }

    partial void OnIsRunningToEndChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    partial void OnIsSeekingChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    public static string FormatSeconds(double seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(seconds / 60):0}:{seconds % 60:00.0} min");

    private void ResetReadout()
    {
        _finishAnnounced = false;
        Progress = 0f;
        ElapsedText = FormatSeconds(0);
        CollisionCount = 0;
        LastEventText = string.Empty;
    }

    // The engine cuts its own stock instance; Load and Stop replace it, so the viewport uploads it anew.
    private void ShowStock()
    {
        _viewport.SetStockMap(_simulation.Stock, _simulation.Result!.Stock.StockBottom);
        _viewport.SetToolProgress(_simulation.SegmentsCompleted, _simulation.ToolPosition);
    }

    private void Announce(string status) => StatusChanged?.Invoke(this, status);
}
