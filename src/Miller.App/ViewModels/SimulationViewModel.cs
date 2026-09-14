// PLACEHOLDER - implemented by T-094 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.ViewModels
// Purpose: Play, pause, stop, step, run-to-end commands with enable rules per state; speed factor
//     bound to slider and text; progress, simulated time, collision count.
// Public interface (names only): sealed partial class SimulationViewModel : ViewModelBase { float
//     SpeedFactor; float Progress; string ElapsedText; int CollisionCount; string LastEventText;
//     IRelayCommand PlayCommand, PauseCommand, StopCommand, StepCommand, RunToEndCommand }
// Depends on: Miller.Application services, CommunityToolkit.Mvvm
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
