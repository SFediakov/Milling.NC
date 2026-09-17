using System.Text.Json;
using Miller.Core.Setup;

namespace Miller.Application.Services;

// Named presets in one JSON document next to the executable (the app root), read once and
// rewritten whole on every change. A missing file means no presets; a corrupt file throws, there
// is no fallback. Names are unique without regard to case; saving an existing name replaces it.
public sealed class PresetService
{
    public const string FileName = "presets.json";
    public const int CurrentSchemaVersion = 1;

    private readonly List<MillingPreset> _presets = new();

    public PresetService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string Directory { get; }

    public string FilePath { get; }

    // Sorted by name; the list instance changes with every reload.
    public IReadOnlyList<MillingPreset> Presets => _presets;

    public static string DefaultDirectory() => AppContext.BaseDirectory;

    public MillingPreset? Find(string name)
        => _presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Load()
    {
        _presets.Clear();
        if (!File.Exists(FilePath))
        {
            return;
        }

        var document = JsonSerializer.Deserialize<PresetsDocument>(File.ReadAllText(FilePath), ProjectSerializer.Options)
            ?? throw new InvalidDataException($"{FilePath} holds no presets object.");
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported presets schema version {document.SchemaVersion}; this build reads version {CurrentSchemaVersion}.");
        }

        foreach (var preset in document.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || Find(preset.Name) is not null)
            {
                throw new InvalidDataException($"{FilePath} holds an empty or duplicate preset name '{preset.Name}'.");
            }

            _presets.Add(preset);
        }

        Sort();
    }

    public void Save(MillingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.Name);
        var existing = Find(preset.Name);
        if (existing is not null)
        {
            _presets.Remove(existing);
        }

        _presets.Add(preset);
        Sort();
        Write();
    }

    public bool Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var existing = Find(name);
        if (existing is null)
        {
            return false;
        }

        _presets.Remove(existing);
        Write();
        return true;
    }

    private void Sort() => _presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private void Write()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var document = new PresetsDocument { SchemaVersion = CurrentSchemaVersion, Presets = _presets.ToList() };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, ProjectSerializer.Options));
    }

    private sealed class PresetsDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<MillingPreset> Presets { get; set; } = new();
    }
}
