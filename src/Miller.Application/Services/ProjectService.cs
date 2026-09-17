using Miller.Core.Setup;

namespace Miller.Application.Services;

// The current project, its file and the dirty flag. ProjectChanged fires on every state change so
// the window title and the panels follow.
public sealed class ProjectService
{
    public MillingProject Current { get; private set; } = MillingProject.Default();

    public string? Path { get; private set; }

    public bool IsDirty { get; private set; }

    public event EventHandler? ProjectChanged;

    public void New()
    {
        Current = MillingProject.Default();
        Path = null;
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Current = ProjectSerializer.Deserialize(File.ReadAllText(path));
        Path = path;
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        if (Path is null)
        {
            throw new InvalidOperationException("The project has no file yet; use SaveAs.");
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, ProjectSerializer.Serialize(Current));
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Save();
    }

    // Every edit raises ProjectChanged, not only the first one: the viewport follows each change.
    public void MarkDirty()
    {
        IsDirty = true;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }
}
