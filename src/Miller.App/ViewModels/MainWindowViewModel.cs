using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.App.Services;
using Miller.Application.Services;

namespace Miller.App.ViewModels;

// Owns the services and the state of the main window. Commands that later tasks implement are
// stubs that only report their name in the status bar.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public const string ReadyStatus = "Ready";
    public const string NotImplementedSuffix = ": not implemented yet";
    public static readonly IReadOnlyList<string> StlExtensions = new[] { "stl" };

    [ObservableProperty]
    private string _statusText = ReadyStatus;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private float _progress;

    public MainWindowViewModel(
        ProjectService project,
        MeshImportService meshImport,
        PipelineService pipeline,
        ExportService export,
        SettingsService settings,
        IFileDialogService dialogs,
        IErrorDialogService errors,
        string appVersion)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        MeshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Export = export ?? throw new ArgumentNullException(nameof(export));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        ErrorDialog = errors ?? throw new ArgumentNullException(nameof(errors));
        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        Project.ProjectChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public event EventHandler? ExitRequested;

    public ProjectService Project { get; }

    public MeshImportService MeshImport { get; }

    public PipelineService Pipeline { get; }

    public ExportService Export { get; }

    public SettingsService Settings { get; }

    public IFileDialogService Dialogs { get; }

    public IErrorDialogService ErrorDialog { get; }

    public string AppVersion { get; }

    public PipelineResult? LastResult { get; private set; }

    public string Title => $"{App.WindowTitle} {AppVersion}{(Project.IsDirty ? " *" : string.Empty)}";

    [RelayCommand]
    private async Task OpenStlAsync()
    {
        var path = await Dialogs.OpenFileAsync("Open STL", StlExtensions, Settings.LastStlDirectory);
        if (path is null)
        {
            return;
        }

        try
        {
            var report = MeshImport.Import(path);
            Project.Current.StlPath = path;
            Project.MarkDirty();
            Settings.LastStlDirectory = Path.GetDirectoryName(path);
            Settings.Save();
            var size = report.Bounds.Size;
            StatusText = string.Create(CultureInfo.InvariantCulture,
                $"{report.TriangleCount} triangles, {size.X:0.000} x {size.Y:0.000} x {size.Z:0.000} mm");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            await ErrorDialog.ShowAsync(ex);
        }
    }

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NewProject() => NotYet("New project");

    [RelayCommand]
    private void OpenProject() => NotYet("Open project");

    [RelayCommand]
    private void SaveProject() => NotYet("Save project");

    [RelayCommand]
    private void SaveProjectAs() => NotYet("Save project as");

    [RelayCommand]
    private void ExportNc() => NotYet("Export NC");

    [RelayCommand]
    private void Generate() => NotYet("Generate");

    [RelayCommand]
    private void CancelGenerate() => NotYet("Cancel");

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
