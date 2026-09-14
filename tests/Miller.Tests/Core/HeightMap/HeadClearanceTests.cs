// PLACEHOLDER - implemented by T-029 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.HeightMap
// Tests for: src/Miller.Core/HeightMap/HeadClearance.cs
// Required cases: slot: cutter 3, head 8, length 6 -> slot floor head-limited with effective tip =
//     top - 6; length 12 -> reachable; open areas never head-limited
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
