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
    public void Save_RejectsNonFiniteValues()
    {
        var service = new SettingsService(_root) { WindowWidth = double.NaN };
        Assert.Throws<ArgumentOutOfRangeException>(service.Save);
        Assert.False(File.Exists(service.FilePath));
    }

    // T-143: the machine panel's connection and field values survive a restart; a file written before
    // the panel existed has no machine section and loads with the machine defaults.
    [Fact]
    public void MachinePreferences_RoundTrip_AndAnOlderFileGetsTheDefaults()
    {
        var machine = new MachinePreferences(MachineConnectionKind.Network, "COM7", 57600, "192.168.5.1", 2323, 0.1f, 750f, 15.5f, 25f, 40f, 3f);
        new SettingsService(_root) { Machine = machine }.Save();
        var loaded = new SettingsService(_root);
        loaded.Load();
        Assert.Equal(machine, loaded.Machine);

        File.WriteAllText(loaded.FilePath, "{ \"WindowWidth\": 800, \"WindowHeight\": 600, \"SpeedFactor\": 2 }");
        var older = new SettingsService(_root) { Machine = machine };
        older.Load();
        Assert.Equal(MachinePreferences.Default, older.Machine);
        Assert.Equal(800, older.WindowWidth);
        Assert.Equal(MachineConnectionKind.Serial, MachinePreferences.Default.Connection);
        Assert.Equal(115200, MachinePreferences.Default.BaudRate);
        Assert.Equal(23, MachinePreferences.Default.NetworkPort);
    }

    [Fact]
    public void DefaultDirectory_IsUnderApplicationData()
    {
        Assert.EndsWith(SettingsService.FolderName, SettingsService.DefaultDirectory());
        Assert.Throws<ArgumentException>(() => new SettingsService(" "));
    }
}
