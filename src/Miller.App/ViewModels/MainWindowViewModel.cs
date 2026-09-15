using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Application.Services;

namespace Miller.App.ViewModels;

// Owns the services and the state of the main window. Menu commands and the child view models are
// added by the tasks that introduce them.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public const string ReadyStatus = "Ready";

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
        string appVersion)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        MeshImport = meshImport ?? throw new ArgumentNullException(nameof(meshImport));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Export = export ?? throw new ArgumentNullException(nameof(export));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        Project.ProjectChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ProjectService Project { get; }

    public MeshImportService MeshImport { get; }

    public PipelineService Pipeline { get; }

    public ExportService Export { get; }

    public SettingsService Settings { get; }

    public string AppVersion { get; }

    public PipelineResult? LastResult { get; private set; }

    public string Title => $"{App.WindowTitle} {AppVersion}{(Project.IsDirty ? " *" : string.Empty)}";
}
