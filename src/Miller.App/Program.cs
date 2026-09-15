using Avalonia;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Core.Setup;

namespace Miller.App;

public static class Program
{
    public const string AppVersion = "Build_1.0.77";
    public const string VersionFlag = "--version";
    public const string ExportFlag = "--export";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == VersionFlag)
        {
            Console.WriteLine(AppVersion);
            return 0;
        }

        if (args.Length == 3 && args[0] == ExportFlag)
        {
            return HeadlessExport(args[1], args[2]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

    // Runs the pipeline without a window: used to verify the Linux build without a display and by the
    // end-to-end test. The STL path in the project is resolved relative to the project file.
    public static int HeadlessExport(string projectPath, string outputPath)
    {
        try
        {
            var project = ProjectSerializer.Deserialize(File.ReadAllText(projectPath));
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
            var import = new MeshImportService();
            foreach (var model in project.Models)
            {
                var stlPath = Path.IsPathRooted(model.StlPath) ? model.StlPath : Path.Combine(projectDirectory, model.StlPath);
                var report = import.Import(stlPath);
                Console.WriteLine($"mesh {report.TriangleCount} triangles, {report.Format}");
            }

            var result = new PipelineService().Run(project, import.Meshes, new ConsoleProgress(), CancellationToken.None);
            var written = new ExportService().Export(result.Toolpath, project, AppVersion, outputPath);
            Console.WriteLine($"written {written}: {result.Statistics.SegmentCount} segments, {result.Statistics.EstimatedMinutes:F1} min");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or KeyNotFoundException
            or Miller.Application.Validation.ValidationException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"export failed: {ex.Message}");
            return 1;
        }
    }

    private sealed class ConsoleProgress : IProgress<ProgressReport>
    {
        private string _lastStage = string.Empty;

        public void Report(ProgressReport value)
        {
            if (value.Stage == _lastStage)
            {
                return;
            }

            _lastStage = value.Stage;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value.Stage} {value.Fraction:0.00}"));
        }
    }
}
