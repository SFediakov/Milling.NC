// PLACEHOLDER - implemented by T-031 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpaths
// Purpose: Ordered list of segments produced by strategies and the linker.
// Public interface (names only): sealed class Toolpath { List<ToolpathSegment> Segments; int Count;
//     void Add(ToolpathSegment); void AddRange(IEnumerable<ToolpathSegment>); BoundingBox Bounds;
//     float TotalLength(MoveKind kind); float TotalLength() }
// Depends on: ToolpathSegment, BoundingBox
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
