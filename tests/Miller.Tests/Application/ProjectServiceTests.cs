using Miller.Application.Services;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Application;

public sealed class ProjectServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-project-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void NewProject_IsDefaultCleanAndUnsaved()
    {
        var service = new ProjectService();
        Assert.Null(service.Path);
        Assert.False(service.IsDirty);
        Assert.Equal(MillingProject.DefaultRoughingStrategyId, service.Current.RoughingStrategyId);
    }

    [Fact]
    public void SaveAs_Load_RoundTripsAndClearsDirty()
    {
        var service = new ProjectService();
        var raised = 0;
        service.ProjectChanged += (_, _) => raised++;
        service.Current.Tool.CutterDiameter = 4.5f;
        service.Current.StlPath = "part.stl";
        service.MarkDirty();
        Assert.True(service.IsDirty);
        Assert.Equal(1, raised);

        var path = Path.Combine(_root, "nested", "part.miller.json");
        service.SaveAs(path);
        Assert.Equal(path, service.Path);
        Assert.False(service.IsDirty);
        Assert.True(File.Exists(path));
        Assert.Equal(2, raised);

        var other = new ProjectService();
        other.Load(path);
        Assert.Equal(4.5f, other.Current.Tool.CutterDiameter);
        Assert.Equal("part.stl", other.Current.StlPath);
        Assert.Equal(path, other.Path);
        Assert.False(other.IsDirty);
    }

    [Fact]
    public void Save_WithoutPath_Throws_AndNewResetsEverything()
    {
        var service = new ProjectService();
        Assert.Throws<InvalidOperationException>(service.Save);

        service.SaveAs(Path.Combine(_root, "a.miller.json"));
        service.Current.Parameters.Stepdown = 9f;
        service.MarkDirty();
        service.MarkDirty();
        service.New();
        Assert.Null(service.Path);
        Assert.False(service.IsDirty);
        Assert.Equal(CuttingParameters.DefaultStepdown, service.Current.Parameters.Stepdown);
    }

    [Fact]
    public void Load_CorruptFile_ThrowsAndKeepsTheCurrentProject()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bad.miller.json");
        File.WriteAllText(path, "{ not json");
        var service = new ProjectService();
        service.Current.Tool.Name = "keep me";
        Assert.ThrowsAny<Exception>(() => service.Load(path));
        Assert.Equal("keep me", service.Current.Tool.Name);
        Assert.Null(service.Path);
    }
}
