using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Services;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;

namespace Miller.App.ViewModels;

// The machine commands and their enable rules. A command that the machine or the input refuses
// leaves its reason in Message; nothing is sent then.
public sealed partial class MachineViewModel
{
    public const string ChoosePortMessage = "Choose or type a serial port.";
    public const string EnterHostMessage = "Enter the host name or address of the controller.";
    public const string NetworkPortMessage = "The network port must be a whole number from 1 to 65535.";
    public const string SaveProgressTitle = "Save progress as";
    public const string ResumeSuffix = "_resume";
    public static readonly IReadOnlyList<string> NcExtensions = new[] { "nc", "gcode", "ngc", "tap", "txt" };

    private bool CanConnect => !IsConnected && !_connecting;

    private bool CanLoadProgram => _toolpath() is not null && !IsJobActive;

    private bool CanOpenFile => !IsJobActive;

    private bool CanStartProgram => _machine.Program is not null && CanRunNow && State == GrblStatus.Idle;

    private bool CanPause => IsReady && (IsJobActive || State is GrblStatus.Run or GrblStatus.Jog);

    private bool CanResume => IsReady && State is GrblStatus.Hold or GrblStatus.Door;

    private bool CanStop => IsReady && (IsJobActive || State is GrblStatus.Run or GrblStatus.Hold or GrblStatus.Jog or GrblStatus.Door);

    private bool CanHome => CanRunNow && State is GrblStatus.Idle or GrblStatus.Alarm;

    private bool CanJog => CanRunNow && State is GrblStatus.Idle or GrblStatus.Jog;

    private bool CanCancelJog => IsReady && State == GrblStatus.Jog;

    private bool CanRunIdle => CanRunNow && State == GrblStatus.Idle;

    private bool CanSaveProgress => _machine.RunJob is { IsActive: false };

    // Console lines go out when nothing runs, or into a program while it is paused.
    private bool CanSend => CanRunNow || Snapshot is { IsReady: true } snapshot && MachineController.IsPausedProgram(snapshot.Job?.Kind, snapshot.Job?.State, snapshot.Status);

    private void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[]
        {
            ConnectCommand, DisconnectCommand, UseToolpathCommand, OpenFileCommand, StartCommand,
            PauseCommand, ResumeCommand, StopCommand, ResetCommand, HomeCommand, UnlockCommand, JogCommand, CancelJogCommand,
            ZeroCommand, GoToZeroCommand, ProbeCommand, OverrideCommand, SendCommand, SetWorkCommand, ReadWorkCommand, SaveProgressCommand, RunMacroCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        try
        {
            var ports = MachineService.SerialPorts();
            if (!ports.SequenceEqual(Ports))
            {
                Ports = ports;
            }

            if (string.IsNullOrEmpty(SerialPort) && ports.Count > 0)
            {
                SerialPort = ports[0];
            }
        }
        catch (MachineLinkException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        Message = null;
        var settings = ConnectionSettings(out var problem);
        if (settings is null)
        {
            Message = problem;
            return;
        }

        SavePreferences();
        _connecting = true;
        Refresh();
        try
        {
            await _machine.ConnectAsync(settings);
        }
        catch (Exception ex) when (ex is MachineLinkException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
        finally
        {
            _connecting = false;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        if (IsJobActive && !await _confirm.ConfirmAsync("A job is running on the machine. Stop it and disconnect?"))
        {
            return;
        }

        Message = null;
        await Task.Run(_machine.Disconnect);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanLoadProgram))]
    private void UseToolpath()
    {
        var toolpath = _toolpath();
        if (toolpath is null)
        {
            return;
        }

        Try(() =>
        {
            _machine.LoadToolpath(toolpath, _project(), _appVersion);
            _programToolpath = toolpath;
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private async Task OpenFileAsync()
    {
        var path = await _dialogs.OpenFileAsync("Open NC program", NcExtensions, _settings.LastExportDirectory);
        if (path is not null)
        {
            Try(() =>
            {
                _machine.LoadFile(path);
                _programToolpath = null;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartProgram))]
    private async Task StartAsync()
    {
        var program = _machine.Program!;
        var question = $"Run {program.Grbl.Name} ({program.Grbl.Count} lines) on {Snapshot?.LinkName}? " +
            "The work zero must be set and the tool must be clear of the stock.";
        if (await _confirm.ConfirmAsync(question))
        {
            Try(_machine.StartProgram);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => Try(_machine.Pause);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => Try(_machine.Resume);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => Try(_machine.Stop);

    [RelayCommand(CanExecute = nameof(IsReady))]
    private void Reset() => Try(_machine.Reset);

    [RelayCommand(CanExecute = nameof(CanHome))]
    private void Home() => Try(_machine.Home);

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private void Unlock() => Try(_machine.Unlock);

    // "X+", "X-", "Y+", "Y-", "Z+", "Z-": one step of the chosen size.
    [RelayCommand(CanExecute = nameof(CanJog))]
    private void Jog(string direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        var axis = Enum.Parse<MachineAxis>(direction[..1]);
        var sign = direction[1] == '-' ? -1 : 1;
        Try(() =>
        {
            _machine.Jog(axis, sign * (double)JogStep, JogFeed);
            SavePreferences();
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancelJog))]
    private void CancelJog() => Try(_machine.CancelJog);

    // "X", "Y", "Z" or "XYZ".
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void Zero(string axes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axes);
        Try(() => _machine.Zero(axes.Contains('X', StringComparison.Ordinal), axes.Contains('Y', StringComparison.Ordinal), axes.Contains('Z', StringComparison.Ordinal)));
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void GoToZero() => Try(_machine.GoToXyZero);

    // "X", "Y", "Z" or "XYZ": the current position reads as the work coordinate fields.
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void SetWork(string axes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axes);
        Try(() => _machine.SetWork(
            axes.Contains('X', StringComparison.Ordinal) ? WorkValueX : null,
            axes.Contains('Y', StringComparison.Ordinal) ? WorkValueY : null,
            axes.Contains('Z', StringComparison.Ordinal) ? WorkValueZ : null));
    }

    // Copies the current work position into the work coordinate fields.
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void ReadWork()
    {
        var work = Snapshot!.Status.WorkPosition;
        WorkValueX = (float)work.X;
        WorkValueY = (float)work.Y;
        WorkValueZ = (float)work.Z;
    }

    [RelayCommand]
    private void ShowListing() => ListingRequested?.Invoke(this, EventArgs.Empty);

    // The interrupted run as a new program (MachineService.SaveProgress); the program file stays.
    [RelayCommand(CanExecute = nameof(CanSaveProgress))]
    private async Task SaveProgressAsync()
    {
        var program = _machine.RunProgram!;
        var name = Path.GetFileNameWithoutExtension(program.Grbl.Name) + ResumeSuffix + "." + NcExtensions[0];
        var path = await _dialogs.SaveFileAsync(SaveProgressTitle, name, NcExtensions, _settings.LastExportDirectory);
        if (path is null)
        {
            return;
        }

        Try(() =>
        {
            _machine.SaveProgress(path);
            StatusChanged?.Invoke(this, $"Progress saved to {path}");
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void Probe()
        => Try(() =>
        {
            _machine.ProbeZ(ProbeThickness, ProbeTravel, ProbeFeed, ProbeRetract);
            SavePreferences();
        });

    // "F-", "F0", "F+" feed -10 %, 100 %, +10 %; "R25", "R50", "R100" rapid; "S-", "S0", "S+" spindle.
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void Override(string key)
    {
        var command = key switch
        {
            "F-" => GrblRealtime.FeedMinus10,
            "F0" => GrblRealtime.FeedReset,
            "F+" => GrblRealtime.FeedPlus10,
            "R25" => GrblRealtime.RapidQuarter,
            "R50" => GrblRealtime.RapidHalf,
            "R100" => GrblRealtime.RapidFull,
            "S-" => GrblRealtime.SpindleMinus10,
            "S0" => GrblRealtime.SpindleReset,
            "S+" => GrblRealtime.SpindlePlus10,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown override."),
        };
        Try(() => _machine.Realtime(command));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (string.IsNullOrWhiteSpace(ConsoleInput))
        {
            return;
        }

        var line = ConsoleInput;
        Try(() =>
        {
            _machine.Send(line);
            ConsoleInput = string.Empty;
        });
    }

    private void Try(Action action)
    {
        Message = null;
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Message = ex.Message;
        }

        Refresh();
    }

    private MachineConnectionSettings? ConnectionSettings(out string? problem)
    {
        problem = null;
        if (IsSerial && string.IsNullOrWhiteSpace(SerialPort))
        {
            problem = ChoosePortMessage;
            return null;
        }

        if (IsNetwork && string.IsNullOrWhiteSpace(Host))
        {
            problem = EnterHostMessage;
            return null;
        }

        if (IsNetwork && (NetworkPort < 1 || NetworkPort > TcpLink.MaxPort || NetworkPort != MathF.Floor(NetworkPort)))
        {
            problem = NetworkPortMessage;
            return null;
        }

        return new MachineConnectionSettings(IsSerial ? MachineConnectionKind.Serial : MachineConnectionKind.Network,
            SerialPort.Trim(), BaudRate, Host.Trim(), IsNetwork ? (int)NetworkPort : TcpLink.DefaultPort);
    }

    private void SavePreferences()
    {
        _settings.Machine = new MachinePreferences(IsSerial ? MachineConnectionKind.Serial : MachineConnectionKind.Network,
            SerialPort.Trim(), BaudRate, Host.Trim(), (int)MathF.Round(NetworkPort), JogStep, JogFeed, ProbeThickness, ProbeTravel, ProbeFeed, ProbeRetract);
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            Message = string.Create(CultureInfo.InvariantCulture, $"Settings not saved: {ex.Message}");
        }
    }
}
