// PLACEHOLDER - implemented by T-035 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpaths
// Purpose: Explicit list of every strategy. Adding a strategy = one new file in Strategies plus one
//     line here. Unknown id throws KeyNotFoundException listing the known ids. Duplicate ids throw
//     at type initialization.
// Public interface (names only): static class StrategyRegistry { static
//     IReadOnlyList<IToolpathStrategy> All; static IToolpathStrategy GetById(string id); static
//     IEnumerable<IToolpathStrategy> ForOperation(MillingOperation op) }
// Depends on: IToolpathStrategy, Strategies/*
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
