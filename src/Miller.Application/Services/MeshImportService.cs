using Miller.Core.Geometry;
using Miller.Core.Io;

namespace Miller.Application.Services;

// Loads an STL through StlReader and keeps the current mesh for the pipeline and the viewport.
public sealed class MeshImportService
{
    public Mesh? CurrentMesh { get; private set; }

    public StlImportReport? Report { get; private set; }

    public bool HasMesh => CurrentMesh is not null;

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

        CurrentMesh = mesh;
        Report = report;
        MeshChanged?.Invoke(this, EventArgs.Empty);
        return report;
    }

    public void Clear()
    {
        CurrentMesh = null;
        Report = null;
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
