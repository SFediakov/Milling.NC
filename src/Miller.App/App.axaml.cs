// PLACEHOLDER - implemented by T-059 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App
// Purpose: Composition root: constructs LogService, SettingsService, ProjectService,
//     MeshImportService, PipelineService, ExportService, SimulationService, AnalysisService, the
//     dialog services, the view models and MainWindow. No DI container.
// Public interface (names only): partial class App : Application { override void Initialize();
//     override void OnFrameworkInitializationCompleted() }
// Depends on: Miller.Application services, MainWindowViewModel, MainWindow, UiTimer
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
