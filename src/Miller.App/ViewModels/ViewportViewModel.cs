using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Core.Geometry;
using Miller.Application.Services;
using Miller.Core.Analysis;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;

namespace Miller.App.ViewModels;

// What the viewport shows. Data is versioned so the renderer uploads only what changed; every
// change raises RequestRender.
public sealed partial class ViewportViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _showModel = true;

    [ObservableProperty]
    private bool _showStock = true;

    [ObservableProperty]
    private bool _showToolpath = true;

    [ObservableProperty]
    private bool _showTool = true;

    public event EventHandler? RequestRender;

    public event EventHandler<string>? GlError;

    // Machine-space meshes, one per model placement.
    public IReadOnlyList<Mesh> Meshes { get; private set; } = Array.Empty<Mesh>();

    public BoundingBox MeshBounds
    {
        get
        {
            var union = BoundingBox.Empty;
            foreach (var mesh in Meshes)
            {
                union = union.Union(mesh.Bounds);
            }

            return union;
        }
    }

    public int MeshVersion { get; private set; }

    public BoundingBox? StockBounds { get; private set; }

    public StockDefinition? StockDefinition { get; private set; }

    public int StockVersion { get; private set; }

    public Toolpath? Toolpath { get; private set; }

    public int ToolpathVersion { get; private set; }

    public int ToolpathProgressIndex { get; private set; }

    public Vector3 ToolPosition { get; private set; }

    // The tool marker follows a connected machine (its work position), with or without a toolpath.
    public bool MachineLive { get; private set; }

    public ToolDefinition? Tool { get; private set; }

    public int ToolVersion { get; private set; }

    public HeightMap? StockMap { get; private set; }

    public float StockFloorZ { get; private set; }

    // Per-cell colors of the stock map (analysis view); null draws the plain stock color.
    public CellCategory[]? StockCategories { get; private set; }

    public int CategoriesVersion { get; private set; }

    public int StockMapVersion { get; private set; }

    // Cells changed since the renderer last synced; applied as a partial buffer update.
    public DirtyRect StockDirty { get; private set; } = DirtyRect.Empty;

    public bool FitPending { get; private set; }

    // Placed bounds of every model the viewport shows, in machine space; the selection indexes it.
    public IReadOnlyList<BoundingBox> ModelBounds { get; private set; } = Array.Empty<BoundingBox>();

    public int SelectedModelIndex { get; private set; } = -1;

    // A ray closer to horizontal than this cannot place a point on the drag plane.
    public const float MinDragRayZ = 1e-4f;

    private int _dragIndex = -1;
    private float _dragPlaneZ;
    private Vector2 _dragLast;

    public event EventHandler? SelectionChanged;

    // Machine-space XY movement of the selected model during a viewport drag.
    public event EventHandler<Vector2>? ModelDragged;

    // Space in the viewport; the simulation panel decides between play and pause.
    public event EventHandler? PlayPauseRequested;

    public void RequestPlayPause() => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    public bool IsDragging => _dragIndex >= 0;

    // Picks the model whose bounds the ray hits first; a miss clears the selection.
    public void Pick(Vector3 origin, Vector3 direction) => Select(Hit(origin, direction).Index);

    // A left press: picks like Pick and, on a hit, starts a drag on the horizontal plane through
    // the hit point, so the model follows the pointer in machine X and Y.
    public bool BeginDrag(Vector3 origin, Vector3 direction)
    {
        var (index, distance) = Hit(origin, direction);
        Select(index);
        if (index < 0)
        {
            return false;
        }

        var hit = origin + direction * distance;
        _dragIndex = index;
        _dragPlaneZ = hit.Z;
        _dragLast = new Vector2(hit.X, hit.Y);
        return true;
    }

    public void DragTo(Vector3 origin, Vector3 direction)
    {
        if (_dragIndex < 0 || _dragIndex != SelectedModelIndex || !PlanePoint(origin, direction, _dragPlaneZ, out var point))
        {
            return;
        }

        var delta = point - _dragLast;
        if (delta == Vector2.Zero)
        {
            return;
        }

        _dragLast = point;
        ModelDragged?.Invoke(this, delta);
    }

    public void EndDrag() => _dragIndex = -1;

    // Where the ray crosses the horizontal plane at z; false for a ray that is parallel to it or
    // crosses it behind the origin.
    public static bool PlanePoint(Vector3 origin, Vector3 direction, float z, out Vector2 point)
    {
        point = default;
        if (MathF.Abs(direction.Z) < MinDragRayZ)
        {
            return false;
        }

        var t = (z - origin.Z) / direction.Z;
        if (t < 0)
        {
            return false;
        }

        point = new Vector2(origin.X + direction.X * t, origin.Y + direction.Y * t);
        return true;
    }

    // Nearest model whose bounds the ray hits; hidden models are not hit.
    private (int Index, float Distance) Hit(Vector3 origin, Vector3 direction)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        if (!ShowModel)
        {
            return (best, bestDistance);
        }

        for (var k = 0; k < ModelBounds.Count; k++)
        {
            var hit = ModelBounds[k].IntersectRay(origin, direction);
            if (hit is float distance && distance < bestDistance)
            {
                bestDistance = distance;
                best = k;
            }
        }

        return (best, bestDistance);
    }

    public void Select(int index)
    {
        if (index < -1 || index >= ModelBounds.Count)
        {
            index = -1;
        }

        if (index == SelectedModelIndex)
        {
            return;
        }

        SelectedModelIndex = index;
        MeshVersion++;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void SetMeshes(IReadOnlyList<Mesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        Meshes = meshes;
        MeshVersion++;
        FitPending = meshes.Count > 0;
        ModelBounds = meshes.Select(m => m.Bounds).ToList();
        if (SelectedModelIndex >= ModelBounds.Count)
        {
            Select(-1);
        }

        Invalidate();
    }

    public void SetStock(BoundingBox? bounds, StockDefinition? definition)
    {
        StockBounds = bounds;
        StockDefinition = definition;
        StockVersion++;
        Invalidate();
    }

    public void SetTool(ToolDefinition? tool)
    {
        Tool = tool;
        ToolVersion++;
        Invalidate();
    }

    public void SetStockMap(HeightMap? stockMap, float floorZ)
    {
        StockMap = stockMap;
        StockFloorZ = floorZ;
        StockMapVersion++;
        StockDirty = DirtyRect.Empty;
        StockCategories = null;
        CategoriesVersion++;
        Invalidate();
    }

    public void SetCategories(CellCategory[]? categories)
    {
        if (categories is not null && StockMap is not null && categories.Length != StockMap.CellCount)
        {
            throw new ArgumentException("Category array does not match the stock map.", nameof(categories));
        }

        StockCategories = categories;
        CategoriesVersion++;
        Invalidate();
    }

    public void SetToolpath(Toolpath? toolpath)
    {
        Toolpath = toolpath;
        ToolpathVersion++;
        ToolpathProgressIndex = 0;
        ToolPosition = toolpath is { Count: > 0 } ? toolpath.Segments[0].Start : Vector3.Zero;
        Invalidate();
    }

    public void MarkStockDirty(DirtyRect dirty)
    {
        if (dirty.IsEmpty)
        {
            return;
        }

        StockDirty = StockDirty.Union(dirty);
        Invalidate();
    }

    public DirtyRect TakeStockDirty()
    {
        var dirty = StockDirty;
        StockDirty = DirtyRect.Empty;
        return dirty;
    }

    public void ApplySimulation(SimulationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        MarkStockDirty(snapshot.Dirty);
        SetToolProgress(snapshot.SegmentsCompleted, snapshot.ToolPosition);
    }

    public void SetToolProgress(int progressIndex, Vector3 toolPosition)
    {
        ToolpathProgressIndex = progressIndex;
        ToolPosition = toolPosition;
        Invalidate();
    }

    public void SetMachinePosition(int progressIndex, Vector3 toolPosition)
    {
        if (MachineLive && progressIndex == ToolpathProgressIndex && toolPosition == ToolPosition)
        {
            return;
        }

        MachineLive = true;
        SetToolProgress(progressIndex, toolPosition);
    }

    public void ClearMachine()
    {
        if (MachineLive)
        {
            MachineLive = false;
            Invalidate();
        }
    }

    // Raised from the render thread path of the control; consumers marshal as needed.
    public void ReportGlError(string message) => GlError?.Invoke(this, message);

    public void RequestFit()
    {
        FitPending = true;
        Invalidate();
    }

    public void ClearFitRequest() => FitPending = false;

    public void Invalidate() => RequestRender?.Invoke(this, EventArgs.Empty);

    partial void OnShowModelChanged(bool value) => Invalidate();

    partial void OnShowStockChanged(bool value) => Invalidate();

    partial void OnShowToolpathChanged(bool value) => Invalidate();

    partial void OnShowToolChanged(bool value) => Invalidate();
}
