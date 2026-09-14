// PLACEHOLDER - implemented by T-096 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Analysis
// Tests for: src/Miller.Core/Analysis/FinalModelAnalyzer.cs
// Required cases: identical maps -> all Ok; stock above model -> RestMaterial with correct volume;
//     stock below -> Gouge; floor cells -> NoModel
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
