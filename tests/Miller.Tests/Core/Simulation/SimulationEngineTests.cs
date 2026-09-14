// PLACEHOLDER - implemented by T-089 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Core.Simulation
// Tests for: src/Miller.Core/Simulation/SimulationEngine.cs
// Required cases: one full-duration step equals RunToEnd cell by cell; many small steps equal one
//     big step; progress monotonic; Reset restores the stock
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
