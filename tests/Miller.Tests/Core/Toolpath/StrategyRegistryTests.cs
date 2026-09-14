// PLACEHOLDER - implemented by T-036 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Toolpath
// Tests for: src/Miller.Core/Toolpath/StrategyRegistry.cs
// Required cases: ids unique and lowercase-hyphen; GetById(missing) throws listing known ids;
//     display names non-empty; after T-044: exactly raster-roughing, raster-finishing,
//     contour-finishing with operations Roughing, Finishing, Finishing
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
