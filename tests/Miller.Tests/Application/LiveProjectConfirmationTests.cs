using Miller.Application.Services;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Application;

// T-168 (user request): the user's project generated with the parallel route units of T-166 and T-167
// against what Build_1.0.118, the last build before them, reported for the same file: 8 passes and 63
// kept collision events at its own settings (0.025 mm cells, far stepdown 0.2, recursion ratio 5). The
// file lives outside the repository and one generation takes about 16 minutes, so the test runs only
// with MILLER_LIVE_PROJECT naming the project file.
public sealed class LiveProjectConfirmationTests
{
    public const int EventsOfBuild118 = 63;

    [Fact]
    public void LiveProject_KeepsTheCollisionCountOfBuild118()
    {
        var path = Environment.GetEnvironmentVariable("MILLER_LIVE_PROJECT");
        Assert.SkipWhen(path is null || !File.Exists(path), "MILLER_LIVE_PROJECT names no project file.");
        var project = ProjectSerializer.Deserialize(File.ReadAllText(path!));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path!))!;
        var import = new MeshImportService();
        foreach (var model in project.Models)
        {
            import.Import(Path.IsPathRooted(model.StlPath) ? model.StlPath : Path.Combine(directory, model.StlPath));
        }

        var result = new PipelineService().Run(project, import.Meshes, null, TestContext.Current.CancellationToken);
        var events = result.Collisions.Report!.Events.Count;
        Assert.True(events == EventsOfBuild118, $"{events} kept events against {EventsOfBuild118} of Build_1.0.118; per pass: {string.Join(", ", result.PassCollisions.Select(p => p.Events))}");
    }
}
