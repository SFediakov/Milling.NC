// PLACEHOLDER - implemented by T-033 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Slicing
// Purpose: One milling step produced by the slicer: a Z level, the operation and the cells that
//     still need material removed at that level.
// Public interface (names only): enum MillingOperation { Roughing, Finishing }; sealed class
//     MillingStep { float Level; MillingOperation Operation; bool[,] Mask; int MaskCount }
// Depends on: none
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
