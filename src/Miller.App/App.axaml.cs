using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Miller.App.Services;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Application.Services;

namespace Miller.App;

// Composition root: every service and view model is constructed here, by hand, in dependency
// order. There is no container. Unhandled exceptions are logged before the process ends.
public partial class App : Avalonia.Application
{
    public const string WindowTitle = "Miller";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = new LogService(LogService.DefaultDirectory());
            AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => log.Error("Unobserved task exception", e.Exception);

            var settings = new SettingsService(SettingsService.DefaultDirectory());
            settings.Load();
            var project = new ProjectService();
            var meshImport = new MeshImportService();
            var pipeline = new PipelineService();
            var export = new ExportService();
            var simulation = new SimulationService();
            var analysis = new AnalysisService();
            var dialogs = new FileDialogService(() => desktop.MainWindow);
            var errors = new ErrorDialogService(log, () => desktop.MainWindow);
            var confirm = new ConfirmDialogService(() => desktop.MainWindow);
            var viewModel = new MainWindowViewModel(project, meshImport, pipeline, export, simulation, analysis, settings, dialogs, errors, confirm,
                handler => new Progress<Miller.Application.Progress.ProgressReport>(handler), log, Program.AppVersion);
            desktop.MainWindow = new MainWindow
            {
                Width = settings.WindowWidth,
                Height = settings.WindowHeight,
                DataContext = viewModel,
            };
            var timer = new UiTimer(simulation, viewModel.Viewport);
            timer.Ticked += (_, snapshot) => viewModel.SimulationPanel.Apply(snapshot);
            timer.Start();
            desktop.Exit += (_, _) => timer.Dispose();
            log.Info($"started {Program.AppVersion}");
            RegisterGpuPreference(log);

            // An STL path on the command line opens that file once the window is up.
            var startupStl = desktop.Args?.FirstOrDefault(a => a.EndsWith(".stl", StringComparison.OrdinalIgnoreCase));
            if (startupStl is not null)
            {
                desktop.MainWindow.Opened += async (_, _) => await viewModel.OpenStlFileAsync(startupStl);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The registry entry only helps machines with two GPUs; a refused write is logged, not fatal.
    private static void RegisterGpuPreference(LogService log)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            log.Error("GPU preference skipped: the process path is unknown", null);
            return;
        }

        try
        {
            log.Info($"GPU preference for {exe}: {GpuPreference.EnsureHighPerformance(exe)}");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            log.Error($"GPU preference for {exe} not written", ex);
        }
    }
}
