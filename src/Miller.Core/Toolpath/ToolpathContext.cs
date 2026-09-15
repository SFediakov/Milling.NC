// PLACEHOLDER - implemented by T-035 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpaths
// Purpose: Everything a strategy needs, prepared by the pipeline.
// Public interface (names only): sealed record ToolpathContext(HeightMap Model, HeightMap Tip,
//     HeightMap EffectiveTip, HeightMap HeadLimit, HeightMap Stock, SlicePlan Plan, ToolDefinition
//     Tool, ToolProfile Profile, CuttingParameters Parameters, float StockTop)
// Depends on: HeightMap, SlicePlan, ToolDefinition, ToolProfile, CuttingParameters
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
