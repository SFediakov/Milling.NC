// PLACEHOLDER - implemented by T-020 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Setup
// Purpose: Aggregate of every setting plus the selected strategy and post-processor ids. This is
//     what a .miller.json file contains.
// Public interface (names only): sealed class MillingProject { const int CurrentSchemaVersion = 1;
//     int SchemaVersion; string StlPath; ToolDefinition Tool; StockDefinition Stock; AxisSetup
//     Axes; CuttingParameters Parameters; string RoughingStrategyId; string FinishingStrategyId;
//     string PostProcessorId; static MillingProject Default() }
// Depends on: ToolDefinition, StockDefinition, AxisSetup, CuttingParameters
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
