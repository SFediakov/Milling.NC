using System.Text.Json;
using Miller.Machine;
using Miller.Machine.Grbl;

namespace Miller.Application.Macros;

// The machine macros (T-204), a cluster of their own: kept in one JSON document beside the settings,
// read once and rewritten whole on every change, the file first so a failed write changes nothing.
// A missing file means no macros; a corrupt file throws InvalidDataException, which the machine tab
// reports while everything else in it keeps working. Names are unique without regard to case.
// Running a macro goes through MachineService.RunMacro.
public sealed class MacroService
{
    public const string FileName = "macros.json";
    public const int CurrentSchemaVersion = 1;
    public const int MaxNameLength = 40;
    public const int MaxLines = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private List<MachineMacro> _macros = new();

    public MacroService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string Directory { get; }

    public string FilePath { get; }

    // Sorted by name; the list instance changes with every change.
    public IReadOnlyList<MachineMacro> Macros => _macros;

    public MachineMacro? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _macros.FirstOrDefault(m => Same(m.Name, name));
    }

    public void Load()
    {
        _macros = new List<MachineMacro>();
        if (!File.Exists(FilePath))
        {
            return;
        }

        MacrosDocument document;
        try
        {
            document = JsonSerializer.Deserialize<MacrosDocument>(File.ReadAllText(FilePath), Options)
                ?? throw new InvalidDataException($"{FilePath} holds no macros object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FilePath} is not a macros document: {ex.Message}", ex);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported macros schema version {document.SchemaVersion}; this build reads version {CurrentSchemaVersion}.");
        }

        var loaded = new List<MachineMacro>();
        foreach (var stored in document.Macros ?? Array.Empty<MachineMacro>())
        {
            MachineMacro macro;
            try
            {
                macro = Create(stored.Name ?? string.Empty, stored.DelayMs, string.Join('\n', stored.Lines ?? Array.Empty<string>()));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"{FilePath}: macro '{stored.Name}': {ex.Message}", ex);
            }

            if (loaded.Any(m => Same(m.Name, macro.Name)))
            {
                throw new InvalidDataException($"{FilePath} holds the macro name '{macro.Name}' twice.");
            }

            loaded.Add(macro);
        }

        _macros = Sorted(loaded);
    }

    // Adds or replaces a macro; `replacing` is the name of the macro being edited, null for a new one.
    public MachineMacro Save(string name, int delayMs, string text, string? replacing)
    {
        var macro = Create(name, delayMs, text);
        var owner = Find(macro.Name);
        if (owner is not null && (replacing is null || !Same(owner.Name, replacing)))
        {
            throw new ArgumentException($"A macro named '{owner.Name}' exists already.");
        }

        var next = Sorted(_macros.Where(m => replacing is null || !Same(m.Name, replacing)).Append(macro));
        Write(next);
        _macros = next;
        return macro;
    }

    public void Delete(string name)
    {
        var macro = Find(name) ?? throw new ArgumentException($"No macro is named '{name}'.");
        var next = _macros.Where(m => !ReferenceEquals(m, macro)).ToList();
        Write(next);
        _macros = next;
    }

    // Lines are kept as typed (blank ones dropped); each must be a line the controller can take.
    public static MachineMacro Create(string name, int delayMs, string text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"A macro name must have 1 to {MaxNameLength} characters.");
        }

        if (delayMs < 0 || delayMs > MachineJob.MaxDelayMs)
        {
            throw new ArgumentException($"The delay between commands must be 0 to {MachineJob.MaxDelayMs} ms, got {delayMs}.");
        }

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > MaxLines)
        {
            throw new ArgumentException($"A macro holds at most {MaxLines} lines, got {lines.Count}.");
        }

        var commands = 0;
        for (var k = 0; k < lines.Count; k++)
        {
            var prepared = GrblProgram.Clean(lines[k], out var problem);
            if (problem is not null)
            {
                throw new ArgumentException($"Line {k + 1} ({lines[k]}): {problem}");
            }

            commands += prepared.Length > 0 ? 1 : 0;
        }

        return commands > 0 ? new MachineMacro(trimmed, delayMs, lines) : throw new ArgumentException("A macro needs at least one command.");
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<MachineMacro> Sorted(IEnumerable<MachineMacro> macros) => macros.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void Write(IReadOnlyList<MachineMacro> macros)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new MacrosDocument(CurrentSchemaVersion, macros), Options));
    }

    private sealed record MacrosDocument(int SchemaVersion, IReadOnlyList<MachineMacro>? Macros);
}
