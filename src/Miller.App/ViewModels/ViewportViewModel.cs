using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
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

    // Machine-space mesh.
    public Mesh? Mesh { get; private set; }

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

    public bool FitPending { get; private set; }

    public void SetMesh(Mesh? mesh)
    {
        Mesh = mesh;
        MeshVersion++;
        FitPending = mesh is not null;
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
