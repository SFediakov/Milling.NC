// PLACEHOLDER - implemented by T-016 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Io
// Tests for: src/Miller.Core/Io/StlReader.cs
// Required cases: fixture: Binary, 4050 triangles, bounds min (3.259,0.477,0) max
//     (23.741,5.477,21.971) within 0.001; ASCII cube text 12 triangles; truncated binary throws
//     InvalidDataException; garbage text throws with line number; fixture file exists
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
