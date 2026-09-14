// PLACEHOLDER - implemented by T-087 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Simulation
// Purpose: Real time to simulated time with a speed factor clamped to [0.1, 1000].
// Public interface (names only): sealed class SimulationClock { const float MinSpeedFactor = 0.1f;
//     const float MaxSpeedFactor = 1000f; float SpeedFactor; bool IsPlaying; double
//     ElapsedSimulated; void Play(); void Pause(); void Reset(); double Advance(double realSeconds)
//     }
// Depends on: none
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
