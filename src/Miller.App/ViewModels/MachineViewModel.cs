using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Miller.App.Services;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;

namespace Miller.App.ViewModels;

// The machine tab and menu: connection, position, the program run with its progress, jog and zero,
// probe, overrides and the console. The controller runs on its own thread; Refresh (10 times a
// second from the application timer, directly from tests) copies its snapshot and the new console
// entries into the properties and re-evaluates the commands, so the enable rules (nothing that moves
// the machine while a job runs, a program only from Idle) follow the machine rather than the clicks.
// Commands are in MachineViewModel.Commands.cs.
public sealed partial class MachineViewModel : ViewModelBase
{
    public const int RefreshMs = 100;
    public const int ConsoleCapacity = 200;
    public const string DisconnectedText = "Disconnected";
    public const string ConnectingText = "Connecting";
    public const string NoProgramText = "No program";
    public const double MinSecondsForEstimate = 2;
    private const string PositionFormat = "0.000";
    private const string NoValue = "-";

    public static readonly IReadOnlyList<string> ConnectionKinds = new[] { "Serial port", "Network" };
    public static readonly IReadOnlyList<float> JogSteps = new[] { 0.01f, 0.1f, 1f, 10f, 50f };

    private readonly MachineService _machine;
    private readonly SettingsService _settings;
    private readonly ViewportViewModel _viewport;
    private readonly IFileDialogService _dialogs;
    private readonly IConfirmDialogService _confirm;
    private readonly Func<Toolpath?> _toolpath;
    private readonly Func<MillingProject> _project;
    private readonly Func<bool> _simulationPlaying;
    private readonly string _appVersion;
    private Toolpath? _programToolpath;
    private long _logSequence;
    private object? _commandKey;
    private LinkState? _lastLink;
    private long _reportedJob;
    private bool _connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(IsNetwork))]
    private int _connectionIndex;

    [ObservableProperty]
    private string _serialPort = string.Empty;

    [ObservableProperty]
    private int _baudRate;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private float _networkPort;

    [ObservableProperty]
    private IReadOnlyList<string> _ports = Array.Empty<string>();

    [ObservableProperty]
    private string _stateText = DisconnectedText;

    [ObservableProperty]
    private string _connectionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private string _workX = NoValue;

    [ObservableProperty]
    private string _workY = NoValue;

    [ObservableProperty]
    private string _workZ = NoValue;

    [ObservableProperty]
    private string _machineX = NoValue;

    [ObservableProperty]
    private string _machineY = NoValue;

    [ObservableProperty]
    private string _machineZ = NoValue;

    [ObservableProperty]
    private string _feedText = NoValue;

    [ObservableProperty]
    private string _spindleText = NoValue;

    [ObservableProperty]
    private string _feedOverrideText = NoValue;

    [ObservableProperty]
    private string _rapidOverrideText = NoValue;

    [ObservableProperty]
    private string _spindleOverrideText = NoValue;

    [ObservableProperty]
    private string _programText = NoProgramText;

    [ObservableProperty]
    private float _progress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _timeText = string.Empty;

    [ObservableProperty]
    private string _jobText = string.Empty;

    [ObservableProperty]
    private bool _jobFailed;

    [ObservableProperty]
    private float _jogStep;

    [ObservableProperty]
    private float _jogFeed;

    [ObservableProperty]
    private float _probeThickness;

    [ObservableProperty]
    private float _probeTravel;

    [ObservableProperty]
    private float _probeFeed;

    [ObservableProperty]
    private float _probeRetract;

    [ObservableProperty]
    private string _consoleInput = string.Empty;

    public MachineViewModel(
        MachineService machine,
        SettingsService settings,
        ViewportViewModel viewport,
        IFileDialogService dialogs,
        IConfirmDialogService confirm,
        Func<Toolpath?> toolpath,
        Func<MillingProject> project,
        Func<bool> simulationPlaying,
        string appVersion)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _toolpath = toolpath ?? throw new ArgumentNullException(nameof(toolpath));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _simulationPlaying = simulationPlaying ?? throw new ArgumentNullException(nameof(simulationPlaying));
        _appVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        var preferences = settings.Machine;
        _connectionIndex = preferences.Connection == MachineConnectionKind.Network ? 1 : 0;
        _serialPort = preferences.SerialPort;
        _baudRate = SerialLink.BaudRates.Contains(preferences.BaudRate) ? preferences.BaudRate : SerialLink.DefaultBaudRate;
        _host = preferences.Host;
        _networkPort = preferences.NetworkPort;
        _jogStep = JogSteps.Contains(preferences.JogStep) ? preferences.JogStep : MachinePreferences.Default.JogStep;
        _jogFeed = preferences.JogFeed;
        _probeThickness = preferences.ProbeThickness;
        _probeTravel = preferences.ProbeTravel;
        _probeFeed = preferences.ProbeFeed;
        _probeRetract = preferences.ProbeRetract;
        RefreshPorts();
    }

    // A program run ended; the main window shows the text in its status bar.
    public event EventHandler<string>? StatusChanged;

    public static IReadOnlyList<int> BaudRates => SerialLink.BaudRates;

    public ObservableCollection<string> ConsoleLines { get; } = new();

    public bool IsSerial => ConnectionIndex == 0;

    public bool IsNetwork => ConnectionIndex == 1;

    public bool IsConnected => _machine.IsConnected;

    public bool HasMessage => Message is not null;

    public bool IsJobActive => Snapshot is { IsJobActive: true };

    private MachineSnapshot? Snapshot => _machine.Snapshot;

    private bool IsReady => Snapshot is { IsReady: true };

    private bool CanRunNow => Snapshot is { CanRun: true };

    private string State => Snapshot is { IsReady: true } snapshot ? snapshot.Status.State : GrblStatus.UnknownState;

    public void Refresh()
    {
        var snapshot = Snapshot;
        ReadLog();
        ShowConnection(snapshot);
        ShowJob(snapshot);
        ShowOnViewport(snapshot);
        var key = (snapshot?.Link, snapshot?.Status.State, snapshot?.Status.SubState, snapshot?.IsJobActive, snapshot?.LineInFlight,
            _machine.Program, _toolpath(), _connecting);
        if (!Equals(key, _commandKey))
        {
            _commandKey = key;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsJobActive));
            NotifyCommands();
        }
    }

    // Stops a running program and closes the connection; the window calls it when it closes.
    public void Close()
    {
        _machine.Disconnect();
        _viewport.ClearMachine();
    }

    private void ReadLog()
    {
        var entries = _machine.Log.Since(_logSequence);
        if (entries.Count == 0)
        {
            return;
        }

        _logSequence = entries[^1].Sequence;
        foreach (var entry in entries)
        {
            ConsoleLines.Add(entry.Kind switch
            {
                LogKind.Sent => $"out  {entry.Text}",
                LogKind.Received => $"in   {entry.Text}",
                LogKind.Error => $"err  {entry.Text}",
                _ => $"--   {entry.Text}",
            });
        }

        while (ConsoleLines.Count > ConsoleCapacity)
        {
            ConsoleLines.RemoveAt(0);
        }
    }

    private void ShowConnection(MachineSnapshot? snapshot)
    {
        var link = snapshot?.Link;
        if (link != _lastLink)
        {
            _lastLink = link;
            if (link == LinkState.Closed && snapshot!.Error is not null)
            {
                Message = snapshot.Error;
            }
        }

        if (snapshot is null || link == LinkState.Closed)
        {
            StateText = _connecting ? ConnectingText : DisconnectedText;
            ConnectionText = string.Empty;
            WorkX = WorkY = WorkZ = MachineX = MachineY = MachineZ = NoValue;
            FeedText = SpindleText = FeedOverrideText = RapidOverrideText = SpindleOverrideText = NoValue;
            return;
        }

        ConnectionText = snapshot.Version is null ? snapshot.LinkName : $"{snapshot.LinkName}, Grbl {snapshot.Version}";
        if (link == LinkState.Connecting)
        {
            StateText = ConnectingText;
            return;
        }

        var status = snapshot.Status;
        StateText = status.State + (status.IsReport(GrblStatus.Hold) && status.SubState >= 0
            ? status.SubState == GrblStatus.HoldComplete ? " (stopped)" : " (stopping)"
            : status.IsReport(GrblStatus.Door) ? " (door open)" : string.Empty);
        WorkX = Format(status.WorkPosition.X);
        WorkY = Format(status.WorkPosition.Y);
        WorkZ = Format(status.WorkPosition.Z);
        MachineX = Format(status.MachinePosition.X);
        MachineY = Format(status.MachinePosition.Y);
        MachineZ = Format(status.MachinePosition.Z);
        FeedText = string.Create(CultureInfo.InvariantCulture, $"{status.Feed:0} mm/min");
        SpindleText = string.Create(CultureInfo.InvariantCulture, $"{status.Spindle:0} rpm");
        FeedOverrideText = $"{status.FeedOverride}%";
        RapidOverrideText = $"{status.RapidOverride}%";
        SpindleOverrideText = $"{status.SpindleOverride}%";
    }

    private void ShowJob(MachineSnapshot? snapshot)
    {
        var program = _machine.Program;
        ProgramText = program is null ? NoProgramText : $"{program.Grbl.Name}, {program.Grbl.Count} lines";
        var job = snapshot?.Job;
        if (job is null)
        {
            Progress = 0;
            ProgressText = TimeText = JobText = string.Empty;
            JobFailed = false;
            return;
        }

        JobFailed = job.State == JobState.Failed;
        if (job.Kind == MachineJobKind.Command)
        {
            JobText = job.State == JobState.Failed ? $"{job.Name}: {job.Message}" : string.Empty;
            return;
        }

        var kind = job.Kind == MachineJobKind.Check ? "Check" : "Program";
        Progress = job.Total > 0 ? (float)job.Acknowledged / job.Total : 0;
        ProgressText = $"{job.Acknowledged} of {job.Total} lines" + (job.SourceLine > 0 ? $", file line {job.SourceLine}" : string.Empty);
        var elapsed = job.Elapsed;
        var remaining = job.IsActive && job.Acknowledged > 0 && elapsed.TotalSeconds >= MinSecondsForEstimate
            ? $", about {Duration(elapsed * ((double)(job.Total - job.Acknowledged) / job.Acknowledged))} left"
            : string.Empty;
        TimeText = $"{Duration(elapsed)} elapsed{remaining}";
        JobText = $"{kind} {job.State.ToString().ToLowerInvariant()}" + (job.Message is null ? string.Empty : $": {job.Message}");
        var id = job.StartTimestamp;
        if (!job.IsActive && id != _reportedJob)
        {
            _reportedJob = id;
            StatusChanged?.Invoke(this, $"{kind} {job.Name} {job.State.ToString().ToLowerInvariant()} after {Duration(elapsed)}");
        }
    }

    // The marker shows the machine's work position; along the generated toolpath the part already
    // answered is drawn as done. The simulation owns the marker while it plays.
    private void ShowOnViewport(MachineSnapshot? snapshot)
    {
        if (snapshot is not { IsReady: true })
        {
            _viewport.ClearMachine();
            return;
        }

        if (_simulationPlaying())
        {
            return;
        }

        var work = snapshot.Status.WorkPosition;
        var index = _viewport.ToolpathProgressIndex;
        if (snapshot.Job is { Kind: MachineJobKind.Program } job && _machine.Program is { FromToolpath: true } program
            && _programToolpath is not null && ReferenceEquals(_programToolpath, _viewport.Toolpath))
        {
            index = program.SegmentsDone(job.Acknowledged);
        }

        _viewport.SetMachinePosition(index, new Vector3((float)work.X, (float)work.Y, (float)work.Z));
    }

    private static string Format(double value) => value.ToString(PositionFormat, CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span)
        => span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
