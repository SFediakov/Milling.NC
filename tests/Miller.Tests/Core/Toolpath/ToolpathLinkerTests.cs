// PLACEHOLDER - implemented by T-038 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Toolpath
// Tests for: src/Miller.Core/Toolpath/ToolpathLinker.cs
// Required cases: two disjoint passes -> exactly retract, rapid, plunge between; adjacent passes
//     joined by one feed; first move starts at safe height; final retract present
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
