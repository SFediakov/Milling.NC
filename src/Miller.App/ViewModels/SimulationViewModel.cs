using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;

namespace Miller.App.ViewModels;

// Play, pause, stop, step and run to end with enable rules per state, the speed factor shared by
// slider and text box and persisted in the settings, progress, simulated time and the collision
// events. The menu binds to the same commands, so the rules exist once.
public sealed partial class SimulationViewModel : ViewModelBase
{
    public const double StepSeconds = 1.0;
    public const string ReadyStatus = "Simulation ready";
    public const string PlayingStatus = "Simulation playing";
    public const string PausedStatus = "Simulation paused";
    public const string StoppedStatus = "Simulation stopped";
    public const string FinishedStatus = "Simulation finished";
    public const string RunningStatus = "Simulating the whole toolpath";
    public const string NotLoadedText = "Generate a toolpath to simulate it.";
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

    public string StateText => !IsLoaded ? NotLoadedText
        : IsRunningToEnd ? RunningStatus
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

    private bool CanPlay => IsLoaded && !IsPlaying && !IsFinished && !IsRunningToEnd;

    private bool CanPause => IsPlaying;

    private bool CanStop => IsLoaded && !IsRunningToEnd;

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
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(StateText));
    }

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
