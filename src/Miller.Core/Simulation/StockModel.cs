// PLACEHOLDER - implemented by T-030 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Simulation
// Purpose: Builds the stock heightmap from StockDefinition: box = all cells at top, cylinder = NaN
//     outside the circle. Auto-fit centers the model in XY with margin and puts the model top at
//     the stock top.
// Public interface (names only): sealed record StockGeometry(HeightMap Map, float StockTop, float
//     StockBottom, BoundingBox Bounds); static class StockModel { static StockGeometry
//     Create(StockDefinition stock, BoundingBox modelBoundsMachine, float cellSize) }
// Depends on: StockDefinition, HeightMap, BoundingBox
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
