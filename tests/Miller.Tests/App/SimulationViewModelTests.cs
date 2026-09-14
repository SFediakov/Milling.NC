// PLACEHOLDER - implemented by T-093 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.App
// Tests for: src/Miller.App/ViewModels/SimulationViewModel.cs and Converters/LogSliderConverter.cs
// Required cases: T-093: converter 0 -> 0.1, 0.25 -> 1, 1 -> 1000, round trip within 1e-4; T-095:
//     command enable rules per state; speed text 5000 clamps to 1000 with message; Stop resets
//     progress
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
