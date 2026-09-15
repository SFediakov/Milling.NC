using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Miller.App.ViewModels;
using Miller.Application.Services;

namespace Miller.App;

// Composition root: every service and view model is constructed here, by hand, in dependency
// order. There is no container.
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
            var settings = new SettingsService(SettingsService.DefaultDirectory());
            settings.Load();
            var project = new ProjectService();
            var meshImport = new MeshImportService();
            var pipeline = new PipelineService();
            var export = new ExportService();
            var viewModel = new MainWindowViewModel(project, meshImport, pipeline, export, settings, Program.AppVersion);
            desktop.MainWindow = new Window
            {
                Title = viewModel.Title,
                Width = settings.WindowWidth,
                Height = settings.WindowHeight,
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
