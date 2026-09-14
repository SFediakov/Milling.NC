// PLACEHOLDER - implemented by T-034 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Slicing
// Tests for: src/Miller.Core/Slicing/Slicer.cs
// Required cases: top 5, min tip 0, stepdown 2 -> levels 3, 1, 0; masks shrink monotonically; flat
//     tip at stock top -> zero roughing levels, one finishing step
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
