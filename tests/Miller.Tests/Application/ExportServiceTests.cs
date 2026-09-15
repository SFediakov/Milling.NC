using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Application;

public sealed class ExportServiceTests : IDisposable
{
    private const string Version = "Build_0.0.0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-export-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static Toolpath OneMove()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -1), MoveKind.Plunge, 200));
        return path;
    }

    [Fact]
    public void Export_WritesUtf8WithoutBomIntoACreatedDirectory()
    {
        var target = Path.Combine(_root, "nested", "part.nc");
        var written = new ExportService().Export(OneMove(), MillingProject.Default(), Version, target);
        Assert.Equal(target, written);
        var bytes = File.ReadAllBytes(written);
        Assert.Equal((byte)'(', bytes[0]);
        Assert.StartsWith($"( Miller {Version} )\n", File.ReadAllText(written));
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void Export_EnforcesThePostProcessorExtension()
    {
        var written = new ExportService().Export(OneMove(), MillingProject.Default(), Version, Path.Combine(_root, "part.txt"));
        Assert.EndsWith("part.nc", written);
        Assert.True(File.Exists(written));
        Assert.Equal("part.NC", ExportService.EnforceExtension("part.NC", ".nc"));
        Assert.Equal("part.nc", ExportService.EnforceExtension("part", ".nc"));
    }

    [Fact]
    public void Export_UnknownPostProcessor_ThrowsBeforeCreatingTheFile()
    {
        var project = MillingProject.Default();
        project.PostProcessorId = "unknown";
        var target = Path.Combine(_root, "part.nc");
        Assert.Throws<KeyNotFoundException>(() => new ExportService().Export(OneMove(), project, Version, target));
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(_root));
    }
}
