using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Core.Geometry;
using Miller.Application.Services;
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

    public ToolDefinition? Tool { get; private set; }

    public int ToolVersion { get; private set; }

    public HeightMap? StockMap { get; private set; }

    public float StockFloorZ { get; private set; }

    public int StockMapVersion { get; private set; }

    // Cells changed since the renderer last synced; applied as a partial buffer update.
    public DirtyRect StockDirty { get; private set; } = DirtyRect.Empty;

    public bool FitPending { get; private set; }

    // Placed bounds of every model the viewport shows, in machine space; the selection indexes it.
    public IReadOnlyList<BoundingBox> ModelBounds { get; private set; } = Array.Empty<BoundingBox>();

    public int SelectedModelIndex { get; private set; } = -1;

    public event EventHandler? SelectionChanged;

    // Space in the viewport; the simulation panel decides between play and pause.
    public event EventHandler? PlayPauseRequested;

    public void RequestPlayPause() => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    // Picks the model whose bounds the ray hits first; a miss clears the selection.
    public void Pick(Vector3 origin, Vector3 direction)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var k = 0; k < ModelBounds.Count; k++)
        {
            var hit = ModelBounds[k].IntersectRay(origin, direction);
            if (hit is float distance && distance < bestDistance)
            {
                bestDistance = distance;
                best = k;
            }
        }

        Select(best);
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
