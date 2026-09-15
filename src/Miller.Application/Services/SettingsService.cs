using System.Text.Json;

namespace Miller.Application.Services;

// User preferences as JSON in the per-user application data folder. A missing file means first run
// and yields the defaults; a corrupt file throws, there is no fallback to defaults.
public sealed class SettingsService
{
    public const string FolderName = "Miller";
    public const string FileName = "settings.json";
    public const double DefaultWindowWidth = 1280;
    public const double DefaultWindowHeight = 720;
    public const float DefaultSpeedFactor = 1f;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SettingsService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string? LastStlDirectory { get; set; }

    public string? LastProjectDirectory { get; set; }

    public string? LastExportDirectory { get; set; }

    public double WindowWidth { get; set; } = DefaultWindowWidth;

    public double WindowHeight { get; set; } = DefaultWindowHeight;

    public float SpeedFactor { get; set; } = DefaultSpeedFactor;

    public static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException($"{FilePath} holds no settings object.");
        LastStlDirectory = document.LastStlDirectory;
        LastProjectDirectory = document.LastProjectDirectory;
        LastExportDirectory = document.LastExportDirectory;
        WindowWidth = document.WindowWidth;
        WindowHeight = document.WindowHeight;
        SpeedFactor = document.SpeedFactor;
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var document = new SettingsDocument(LastStlDirectory, LastProjectDirectory, LastExportDirectory, WindowWidth, WindowHeight, SpeedFactor);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, Options));
    }

    private sealed record SettingsDocument(
        string? LastStlDirectory,
        string? LastProjectDirectory,
        string? LastExportDirectory,
        double WindowWidth,
        double WindowHeight,
        float SpeedFactor);
}
