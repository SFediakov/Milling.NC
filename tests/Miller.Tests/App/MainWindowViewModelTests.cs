// PLACEHOLDER - implemented by T-065 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.App
// Tests for: src/Miller.App/ViewModels/MainWindowViewModel.cs
// Required cases: T-065: OpenStl with the fake dialog returning the fixture sets status and project
//     StlPath; null leaves state unchanged; T-072: New, Open, Save, SaveAs, ExportNc (enabled only
//     with a toolpath), Exit asks to save when dirty; title shows * when dirty
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
