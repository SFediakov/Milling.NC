// PLACEHOLDER - implemented by T-042 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Toolpaths
// Tests for: src/Miller.Core/Toolpath/MarchingSquares.cs
// Required cases: circular hill -> one closed loop, perimeter within 5 percent of 2 pi r; two hills
//     -> two loops; level above max -> none; NaN border closes loops
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
