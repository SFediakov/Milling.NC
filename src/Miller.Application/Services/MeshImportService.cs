using Miller.Core.Geometry;
using Miller.Core.Io;

namespace Miller.Application.Services;

// Loads STL files through StlReader and keeps one mesh per model placement of the project, in the
// same order; the main view model adds and removes placements and meshes together.
public sealed class MeshImportService
{
    private readonly List<Mesh> _meshes = new();
    private readonly List<StlImportReport> _reports = new();

    public IReadOnlyList<Mesh> Meshes => _meshes;

    public IReadOnlyList<StlImportReport> Reports => _reports;

    public bool HasMesh => _meshes.Count > 0;

    // True once every placement of the project has its mesh; false in the moment between adding
    // or removing a placement and its mesh.
    public bool Matches(Miller.Core.Setup.MillingProject project) => project is not null && _meshes.Count > 0 && _meshes.Count == project.Models.Count;

    public IReadOnlyList<BoundingBox> Bounds => _meshes.Select(m => m.Bounds).ToList();

    public event EventHandler? MeshChanged;

    public StlImportReport Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var (mesh, report) = StlReader.Read(path);
        if (mesh.TriangleCount == 0)
        {
            throw new InvalidDataException($"{path} holds no usable triangles ({report.DegenerateCount} degenerate).");
        }

        if (!IsFinite(mesh.Bounds.Min) || !IsFinite(mesh.Bounds.Max))
        {
            throw new InvalidDataException($"{path} has non-finite coordinates: {mesh.Bounds.Min} to {mesh.Bounds.Max}.");
        }

        _meshes.Add(mesh);
        _reports.Add(report);
        MeshChanged?.Invoke(this, EventArgs.Empty);
        return report;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _meshes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _meshes.RemoveAt(index);
        _reports.RemoveAt(index);
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _meshes.Clear();
        _reports.Clear();
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
