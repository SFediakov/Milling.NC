using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Tests.Core.GCode;
using Xunit;

namespace Miller.Tests.App;

// The same run is verified on the Linux build inside Docker:
//   docker build -t miller-build .
//   docker run --rm miller-build sh -c "dist/linux-x64/Miller.sh --export samples/heart.miller.json /tmp/heart.nc >/dev/null && cat /tmp/heart.nc" > out/heart-linux.nc
//   diff <(tail -n +2 out/heart-linux.nc) <(tail -n +2 tests/Miller.Tests/Golden/heart_grbl.nc)
// The diff must be empty; the first line (version comment) is skipped on both sides.
public sealed class EndToEndTests
{
    private const string TestVersion = "Build_0.0.0";

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SampleProjectPath => Path.Combine(RepoRoot, "samples", "heart.miller.json");

    [Fact]
    public void SampleProject_ReproducesTheGoldenFile()
    {
        var project = ProjectSerializer.Deserialize(File.ReadAllText(SampleProjectPath));
        var import = new MeshImportService();
        import.Import(Path.Combine(Path.GetDirectoryName(SampleProjectPath)!, project.StlPath));
        var result = new PipelineService().Run(project, import.CurrentMesh!, null, CancellationToken.None);

        var directory = Path.Combine(Path.GetTempPath(), $"miller-e2e-{Guid.NewGuid():N}");
        try
        {
            var written = new ExportService().Export(result.Toolpath, project, TestVersion, Path.Combine(directory, "heart.nc"));
            var actual = WithoutVersionLine(File.ReadAllText(written));
            var expected = WithoutVersionLine(File.ReadAllText(GrblPostProcessorTests.GoldenPath("heart_grbl.nc")));
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void HeadlessExport_WritesTheFileAndReportsFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"miller-cli-{Guid.NewGuid():N}");
        try
        {
            var output = Path.Combine(directory, "heart.nc");
            Assert.Equal(0, Miller.App.Program.HeadlessExport(SampleProjectPath, output));
            Assert.StartsWith("( Miller Build_", File.ReadAllText(output));
            Assert.Equal(1, Miller.App.Program.HeadlessExport(Path.Combine(directory, "missing.json"), output));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string WithoutVersionLine(string text) => text[(text.IndexOf('\n') + 1)..];
}
