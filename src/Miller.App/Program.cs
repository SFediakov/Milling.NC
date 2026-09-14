// PLACEHOLDER - implemented by T-006 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App
// Purpose: Entry point. Holds AppVersion (Build_Y.Z.X, patch incremented on every app change). CLI:
//     --version prints the version; --export <project.json> <out.nc> runs the pipeline without a
//     window (T-055). Otherwise starts Avalonia with UsePlatformDetect().
// Public interface (names only): static class Program { const string AppVersion = "Build_1.0.0";
//     static int Main(string[] args); static AppBuilder BuildAvaloniaApp() }
// Depends on: App, PipelineService, ExportService, ProjectSerializer, StlReader
// Must not depend on: platform conditionals (#if, RuntimeInformation)
