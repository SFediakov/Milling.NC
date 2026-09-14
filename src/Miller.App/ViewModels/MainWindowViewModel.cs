// PLACEHOLDER - implemented by T-059 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.ViewModels
// Purpose: Menu commands, status text, busy state and progress; owns the child view models.
//     Commands: OpenStl, NewProject, OpenProject, SaveProject, SaveProjectAs, ExportNc, Exit,
//     Generate, CancelGenerate, ResetCamera, ShowAbout.
// Public interface (names only): sealed partial class MainWindowViewModel : ViewModelBase { string
//     Title; string StatusText; bool IsBusy; float Progress; ToolSettingsViewModel Tool;
//     StockSettingsViewModel Stock; AxisSettingsViewModel Axes; CuttingParametersViewModel Cutting;
//     StrategySelectionViewModel Strategy; SimulationViewModel Simulation; AnalysisViewModel
//     Analysis; ViewportViewModel Viewport; PipelineResult? LastResult }
// Depends on: Miller.Application services, CommunityToolkit.Mvvm
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
