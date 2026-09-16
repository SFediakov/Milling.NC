using System.Numerics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.App.Services;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Io;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Owns the services, the child panels and the state of the main window. Project, export and exit
// commands live in MainWindowViewModel.Commands.cs. Progress reports from the pipeline thread reach
// the view model through the injected progress factory: Progress<T> bound to the UI thread in the
// application, a synchronous progress in tests, so the order of updates is deterministic in both.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public const string ReadyStatus = "Ready";
    public const string CancelledStatus = "Toolpath generation cancelled";
    public static readonly IReadOnlyList<string> StlExtensions = new[] { "stl" };

    [ObservableProperty]
    private string _statusText = ReadyStatus;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private float _progress;

    private readonly Func<Action<ProgressReport>, IProgress<ProgressReport>> _progressFactory;
    private CancellationTokenSource? _generation;
    private IReadOnlyList<Mesh> _sceneSourceMeshes = Array.Empty<Mesh>();
    private IReadOnlyList<Matrix4x4> _sceneMatrices = Array.Empty<Matrix4x4>();
    private PipelineResult? _lastResult;

    public MainWindowViewModel(
        ProjectService project,
        MeshImportService meshImport,
        PipelineService pipeline,
        ExportService export,
        SimulationService simulation,
        AnalysisService analysis,
        SettingsService settings,
        IFileDialogService dialogs,
        IErrorDialogService errors,
        IConfirmDialogService confirm,
        Func<Action<ProgressReport>, IProgress<ProgressReport>> progressFactory,
        LogService log,
        string appVersion)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        MeshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Export = export ?? throw new ArgumentNullException(nameof(export));
        Simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        AnalysisRunner = analysis ?? throw new ArgumentNullException(nameof(analysis));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        ErrorDialog = errors ?? throw new ArgumentNullException(nameof(errors));
        Confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _progressFactory = progressFactory ?? throw new ArgumentNullException(nameof(progressFactory));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        Project.ProjectChanged += (_, _) => OnPropertyChanged(nameof(Title));
        MeshImport.MeshChanged += (_, _) =>
        {
            GenerateCommand.NotifyCanExecuteChanged();
            UpdateViewportScene();
        };
        Tool = new ToolSettingsViewModel(Project);
        Stock = new StockSettingsViewModel(Project, MeshImport);
        Axes = new AxisSettingsViewModel(Project, MeshImport);
        Cutting = new CuttingParametersViewModel(Project);
        Strategy = new StrategySelectionViewModel(Project, GenerateCommand, CancelGenerateCommand);
        Models = new ModelsViewModel(Project, MeshImport, OpenStlCommand);
        Viewport = new ViewportViewModel();
        SimulationPanel = new SimulationViewModel(Simulation, Settings, Viewport);
        SimulationPanel.StatusChanged += (_, status) => StatusText = status;
        SimulationPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SimulationViewModel.IsWorking))
            {
                GenerateCommand.NotifyCanExecuteChanged();
            }
        };
        Viewport.PlayPauseRequested += (_, _) => SimulationPanel.TogglePlayPause();
        Analysis = new AnalysisViewModel(AnalysisRunner, Viewport, Simulation);
        SimulationPanel.PlaybackStarted += (_, _) => Analysis.ShowFinalModel = false;
        Models.SelectionChanged += (_, _) => Viewport.Select(Models.SelectedIndex);
        Viewport.SelectionChanged += (_, _) => Models.SelectedIndex = Viewport.SelectedModelIndex;
        Viewport.GlError += (_, message) =>
        {
            Log.Error(message, null);
            StatusText = message;
        };
        Project.ProjectChanged += (_, _) => UpdateViewportScene();
    }

    public event EventHandler? ExitRequested;

    public event EventHandler? ToolpathGenerated;

    public event EventHandler? AboutRequested;

    public ProjectService Project { get; }

    public MeshImportService MeshImport { get; }

    public PipelineService Pipeline { get; }

    public ExportService Export { get; }

    public SimulationService Simulation { get; }

    public AnalysisService AnalysisRunner { get; }

    public SettingsService Settings { get; }

    public IFileDialogService Dialogs { get; }

    public IErrorDialogService ErrorDialog { get; }

    public IConfirmDialogService Confirm { get; }

    public LogService Log { get; }

    public string AppVersion { get; }

    public ToolSettingsViewModel Tool { get; }

    public StockSettingsViewModel Stock { get; }

    public AxisSettingsViewModel Axes { get; }

    public CuttingParametersViewModel Cutting { get; }

    public StrategySelectionViewModel Strategy { get; }

    public ModelsViewModel Models { get; }

    public SimulationViewModel SimulationPanel { get; }

    public AnalysisViewModel Analysis { get; }

    public ViewportViewModel Viewport { get; }

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

    private bool CanGenerate => MeshImport.Matches(Project.Current) && !IsBusy && !SimulationPanel.IsWorking;

    [RelayCommand]
    private async Task OpenStlAsync()
    {
        var path = await Dialogs.OpenFileAsync("Open STL", StlExtensions, Settings.LastStlDirectory);
        if (path is null)
        {
            return;
        }

        await AddModelAsync(path);
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        _generation = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        var progress = _progressFactory(r =>
        {
            Progress = r.Fraction;
            StatusText = r.Message;
        });
        try
        {
            var result = await Pipeline.RunAsync(Project.Current, MeshImport.Meshes, progress, _generation.Token);
            LastResult = result;
            Strategy.ShowResult(result);
            Viewport.SetToolpath(result.Toolpath);
            Simulation.Load(result);
            SimulationPanel.OnLoadedChanged();
            Analysis.SetPipeline(result);
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
        SimulationPanel.Refresh();
    }

    public Task<bool> OpenStlFileAsync(string path) => AddModelAsync(path);

    // Adds a model to the project: mesh and placement together, the new model selected.
    private async Task<bool> AddModelAsync(string path)
    {
        try
        {
            Project.Current.Models.Add(new ModelPlacement { StlPath = path });
            StlImportReport report;
            try
            {
                report = MeshImport.Import(path);
            }
            catch
            {
                Project.Current.Models.RemoveAt(Project.Current.Models.Count - 1);
                throw;
            }

            Project.MarkDirty();
            Settings.LastStlDirectory = Path.GetDirectoryName(path);
            Settings.Save();
            ClearResult();
            UpdateViewportScene();
            Models.SelectedIndex = Project.Current.Models.Count - 1;
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

    // Loads the meshes of an opened project; a placement whose file cannot be read is dropped from
    // the project after the error dialog, so meshes and placements stay aligned.
    private async Task LoadProjectMeshesAsync(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        for (var k = 0; k < Project.Current.Models.Count;)
        {
            var stl = Project.Current.Models[k].StlPath;
            var resolved = Path.IsPathRooted(stl) ? stl : Path.Combine(directory, stl);
            try
            {
                MeshImport.Import(resolved);
                k++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                await ErrorDialog.ShowAsync(ex);
                Project.Current.Models.RemoveAt(k);
                Project.MarkDirty();
            }
        }

        UpdateViewportScene();
    }

    [RelayCommand]
    private void ResetCamera() => Viewport.RequestFit();

    [RelayCommand]
    private void ToggleModel() => Viewport.ShowModel = !Viewport.ShowModel;

    [RelayCommand]
    private void ToggleStock() => Viewport.ShowStock = !Viewport.ShowStock;

    [RelayCommand]
    private void ToggleToolpath() => Viewport.ShowToolpath = !Viewport.ShowToolpath;

    [RelayCommand]
    private void ToggleTool() => Viewport.ShowTool = !Viewport.ShowTool;

    // The viewport shows the model in machine space with the stock placed around it; both follow
    // the axis and stock settings, so any project change recomputes them.
    // A new project state invalidates the last toolpath and the stock it was cut from.
    private void ClearResult()
    {
        LastResult = null;
        Strategy.Clear();
        Simulation.Unload();
        Analysis.SetPipeline(null);
        Viewport.SetToolpath(null);
        Viewport.SetStockMap(null, 0f);
        SimulationPanel.OnLoadedChanged();
    }

    // Runs on every project edit; the mesh is transformed and uploaded again only when its source or
    // the axis transform changed, so typing in a panel does not refit the camera.
    private void UpdateViewportScene()
    {
        Viewport.SetTool(Project.Current.Tool);
        if (!MeshImport.Matches(Project.Current) || !Project.Current.Axes.IsPermutation)
        {
            if (Viewport.Meshes.Count > 0)
            {
                Viewport.SetMeshes(Array.Empty<Mesh>());
            }

            _sceneSourceMeshes = Array.Empty<Mesh>();
            Viewport.SetStock(null, null);
            return;
        }

        var meshes = MeshImport.Meshes;
        var stock = Project.Current.Stock;
        var matrices = ModelLayout.MachineMatrices(Project.Current, MeshImport.Bounds);
        if (!SameScene(meshes, matrices))
        {
            _sceneSourceMeshes = meshes.ToList();
            _sceneMatrices = matrices;
            Viewport.SetMeshes(meshes.Select((mesh, k) => mesh.Transform(matrices[k])).ToList());
        }

        var corner = AxisSetup.StockCorner(Viewport.MeshBounds, stock);
        var bounds = new BoundingBox(corner, corner + AxisSetup.StockBoundingSize(stock));
        Viewport.SetStock(bounds, stock);
    }

    private bool SameScene(IReadOnlyList<Mesh> meshes, IReadOnlyList<Matrix4x4> matrices)
    {
        if (meshes.Count != _sceneSourceMeshes.Count)
        {
            return false;
        }

        for (var k = 0; k < meshes.Count; k++)
        {
            if (!ReferenceEquals(meshes[k], _sceneSourceMeshes[k]) || matrices[k] != _sceneMatrices[k])
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    private void ShowAbout() => AboutRequested?.Invoke(this, EventArgs.Empty);

}
