using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.App.Services;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Application.Validation;

namespace Miller.App.ViewModels;

// Owns the services, the child panels and the state of the main window. Project, export and exit
// commands live in MainWindowViewModel.Commands.cs.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public const string ReadyStatus = "Ready";
    public const string CancelledStatus = "Toolpath generation cancelled";
    public const string NotImplementedSuffix = ": not implemented yet";
    public static readonly IReadOnlyList<string> StlExtensions = new[] { "stl" };

    [ObservableProperty]
    private string _statusText = ReadyStatus;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private float _progress;

    private CancellationTokenSource? _generation;
    private PipelineResult? _lastResult;

    public MainWindowViewModel(
        ProjectService project,
        MeshImportService meshImport,
        PipelineService pipeline,
        ExportService export,
        SettingsService settings,
        IFileDialogService dialogs,
        IErrorDialogService errors,
        IConfirmDialogService confirm,
        string appVersion)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        MeshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Export = export ?? throw new ArgumentNullException(nameof(export));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        ErrorDialog = errors ?? throw new ArgumentNullException(nameof(errors));
        Confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        Project.ProjectChanged += (_, _) => OnPropertyChanged(nameof(Title));
        MeshImport.MeshChanged += (_, _) => GenerateCommand.NotifyCanExecuteChanged();
        Tool = new ToolSettingsViewModel(Project);
        Stock = new StockSettingsViewModel(Project, MeshImport);
        Axes = new AxisSettingsViewModel(Project, MeshImport);
        Cutting = new CuttingParametersViewModel(Project);
        Strategy = new StrategySelectionViewModel(Project, GenerateCommand, CancelGenerateCommand);
    }

    public event EventHandler? ExitRequested;

    public event EventHandler? ToolpathGenerated;

    public ProjectService Project { get; }

    public MeshImportService MeshImport { get; }

    public PipelineService Pipeline { get; }

    public ExportService Export { get; }

    public SettingsService Settings { get; }

    public IFileDialogService Dialogs { get; }

    public IErrorDialogService ErrorDialog { get; }

    public IConfirmDialogService Confirm { get; }

    public string AppVersion { get; }

    public ToolSettingsViewModel Tool { get; }

    public StockSettingsViewModel Stock { get; }

    public AxisSettingsViewModel Axes { get; }

    public CuttingParametersViewModel Cutting { get; }

    public StrategySelectionViewModel Strategy { get; }

    public PipelineResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                ExportNcCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Title => $"{App.WindowTitle} {AppVersion}{(Project.IsDirty ? " *" : string.Empty)}";

    private bool CanGenerate => MeshImport.HasMesh && !IsBusy;

    [RelayCommand]
    private async Task OpenStlAsync()
    {
        var path = await Dialogs.OpenFileAsync("Open STL", StlExtensions, Settings.LastStlDirectory);
        if (path is null)
        {
            return;
        }

        await ImportStlAsync(path, markDirty: true);
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        _generation = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        var progress = new Progress<ProgressReport>(r =>
        {
            Progress = r.Fraction;
            StatusText = r.Message;
        });
        try
        {
            var result = await Pipeline.RunAsync(Project.Current, MeshImport.CurrentMesh!, progress, _generation.Token);
            LastResult = result;
            Strategy.ShowResult(result);
            StatusText = string.Create(CultureInfo.InvariantCulture,
                $"Toolpath ready: {result.Statistics.SegmentCount} segments, {result.Statistics.EstimatedMinutes:0.0} min");
            ToolpathGenerated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText = CancelledStatus;
        }
        catch (Exception ex) when (ex is ValidationException or ArgumentException or KeyNotFoundException or InvalidDataException)
        {
            StatusText = ReadyStatus;
            await ErrorDialog.ShowAsync(ex);
        }
        finally
        {
            _generation.Dispose();
            _generation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelGenerate() => _generation?.Cancel();

    partial void OnIsBusyChanged(bool value)
    {
        GenerateCommand.NotifyCanExecuteChanged();
        CancelGenerateCommand.NotifyCanExecuteChanged();
        ExportNcCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> ImportStlAsync(string path, bool markDirty)
    {
        try
        {
            var report = MeshImport.Import(path);
            Project.Current.StlPath = path;
            if (markDirty)
            {
                Project.MarkDirty();
            }

            Settings.LastStlDirectory = Path.GetDirectoryName(path);
            Settings.Save();
            LastResult = null;
            Strategy.Clear();
            var size = report.Bounds.Size;
            StatusText = string.Create(CultureInfo.InvariantCulture,
                $"{report.TriangleCount} triangles, {size.X:0.000} x {size.Y:0.000} x {size.Z:0.000} mm");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            await ErrorDialog.ShowAsync(ex);
            return false;
        }
    }

    // Stubs replaced by T-073 (about), T-084 (view) and T-094 (simulation).
    [RelayCommand]
    private void ResetCamera() => NotYet("Reset camera");

    [RelayCommand]
    private void ToggleModel() => NotYet("Show model");

    [RelayCommand]
    private void ToggleStock() => NotYet("Show stock");

    [RelayCommand]
    private void ToggleToolpath() => NotYet("Show toolpath");

    [RelayCommand]
    private void ToggleTool() => NotYet("Show tool");

    [RelayCommand]
    private void Play() => NotYet("Play");

    [RelayCommand]
    private void Pause() => NotYet("Pause");

    [RelayCommand]
    private void Stop() => NotYet("Stop");

    [RelayCommand]
    private void RunToEnd() => NotYet("Run to end");

    [RelayCommand]
    private void ShowAbout() => NotYet("About");

    private void NotYet(string name) => StatusText = name + NotImplementedSuffix;
}
