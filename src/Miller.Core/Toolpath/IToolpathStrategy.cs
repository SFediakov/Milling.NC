// PLACEHOLDER - implemented by T-035 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpaths
// Purpose: The exchangeable routing algorithm. Implementations live in Toolpath/Strategies and are
//     listed in StrategyRegistry.
// Public interface (names only): interface IToolpathStrategy { string Id; string DisplayName;
//     MillingOperation Operation; Toolpath Generate(ToolpathContext context, IProgress<float>?
//     progress, CancellationToken cancellation) }
// Depends on: ToolpathContext, Toolpath, MillingOperation
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
