// PLACEHOLDER - implemented by T-092 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Services
// Purpose: 60 Hz DispatcherTimer measuring real elapsed time with Stopwatch; calls
//     SimulationService.Advance and forwards the snapshot to ViewportViewModel; idle when not
//     playing.
// Public interface (names only): sealed class UiTimer : IDisposable { const int FramesPerSecond =
//     60; UiTimer(SimulationService simulation, ViewportViewModel viewport, SimulationViewModel
//     simulationVm); void Start(); void Stop() }
// Depends on: SimulationService, ViewportViewModel
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
