// PLACEHOLDER - implemented by T-037 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpath
// Purpose: Verifies that no feed or plunge segment goes below the effective tip map by more than
//     the tolerance. Used by every strategy test and by the analysis.
// Public interface (names only): readonly record struct GougeViolation(int SegmentIndex, Vector3
//     Position, float Depth); static class GougeChecker { static IReadOnlyList<GougeViolation>
//     Verify(Toolpath toolpath, HeightMap effectiveTip, float tolerance) }
// Depends on: Toolpath, HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
