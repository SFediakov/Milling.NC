using System.Text.Json;
using Miller.Application.Services;
using Xunit;

namespace Miller.Tests.Application;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-settings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var service = new SettingsService(_root);
        service.Load();
        Assert.Null(service.LastStlDirectory);
        Assert.Equal(SettingsService.DefaultWindowWidth, service.WindowWidth);
        Assert.Equal(SettingsService.DefaultWindowHeight, service.WindowHeight);
        Assert.Equal(SettingsService.DefaultSpeedFactor, service.SpeedFactor);
        Assert.Equal(Path.Combine(_root, SettingsService.FileName), service.FilePath);
        Assert.False(File.Exists(service.FilePath));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryField()
    {
        var service = new SettingsService(_root)
        {
            LastStlDirectory = "C:/models",
            LastProjectDirectory = "C:/projects",
            LastExportDirectory = "C:/nc",
            WindowWidth = 1600,
            WindowHeight = 900,
            SpeedFactor = 25f,
        };
        service.Save();
        Assert.True(File.Exists(service.FilePath));

        var loaded = new SettingsService(_root);
        loaded.Load();
        Assert.Equal("C:/models", loaded.LastStlDirectory);
        Assert.Equal("C:/projects", loaded.LastProjectDirectory);
        Assert.Equal("C:/nc", loaded.LastExportDirectory);
        Assert.Equal(1600, loaded.WindowWidth);
        Assert.Equal(900, loaded.WindowHeight);
        Assert.Equal(25f, loaded.SpeedFactor);
    }

    [Fact]
    public void CorruptFile_Throws()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, SettingsService.FileName), "{ broken");
        Assert.Throws<JsonException>(() => new SettingsService(_root).Load());
        File.WriteAllText(Path.Combine(_root, SettingsService.FileName), "null");
        Assert.Throws<InvalidDataException>(() => new SettingsService(_root).Load());
    }

    [Fact]
    public void DefaultDirectory_IsUnderApplicationData()
    {
        Assert.EndsWith(SettingsService.FolderName, SettingsService.DefaultDirectory());
        Assert.Throws<ArgumentException>(() => new SettingsService(" "));
    }
}
