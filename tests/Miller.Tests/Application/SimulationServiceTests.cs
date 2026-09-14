// PLACEHOLDER - implemented by T-091 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Application
// Tests for: src/Miller.Application/Services/SimulationService.cs
// Required cases: play then advance changes the stock; Stop restores it; events accumulate; speed
//     factor forwarded and clamped; RunToEnd finishes
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
