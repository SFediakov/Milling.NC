// PLACEHOLDER - implemented by T-038 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpaths
// Purpose: Joins passes into one toolpath: retract to safe height, rapid in XY, plunge to the next
//     pass start when passes are not adjacent; final retract.
// Public interface (names only): static class ToolpathLinker { static Toolpath
//     Link(IReadOnlyList<Toolpath> passes, CuttingParameters parameters, float safeHeight) }
// Depends on: Toolpath, CuttingParameters
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
