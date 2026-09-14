// PLACEHOLDER - implemented by T-079 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.ViewModels
// Purpose: What the viewport shows: flags (model, stock, toolpath, tool), data references (mesh,
//     stock map, toolpath, tool position, category array), camera reset request, RequestRender
//     event.
// Public interface (names only): sealed partial class ViewportViewModel : ViewModelBase { bool
//     ShowModel; bool ShowStock; bool ShowToolpath; bool ShowTool; Mesh? Mesh; HeightMap? StockMap;
//     CellCategory[]? Categories; Toolpath? Toolpath; int ToolpathProgressIndex; Vector3
//     ToolPosition; ToolDefinition? Tool; StockGeometry? Stock; event Action? RequestRender; void
//     ApplySnapshot(SimulationSnapshot snapshot) }
// Depends on: Miller.Application services, CommunityToolkit.Mvvm
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
