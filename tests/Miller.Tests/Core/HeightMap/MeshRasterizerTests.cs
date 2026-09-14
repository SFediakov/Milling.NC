// PLACEHOLDER - implemented by T-024 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.HeightMap
// Tests for: src/Miller.Core/HeightMap/MeshRasterizer.cs
// Required cases: box 10x10x5 at 0.5 gives 400 cells at 5 and floor elsewhere; fixture max 21.971
//     within 0.01; no gaps between adjacent triangles; RasterizeDownwardFacing on the fixture is
//     non-empty
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
