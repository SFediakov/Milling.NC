// PLACEHOLDER - implemented by T-060 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Services
// Purpose: User preferences as JSON in the per-user application data folder (Miller/settings.json).
//     A corrupt file throws; there is no fallback to defaults.
// Public interface (names only): sealed class SettingsService { const string FileName =
//     "settings.json"; SettingsService(string directory); string? LastStlDirectory; string?
//     LastProjectDirectory; string? LastExportDirectory; double WindowWidth; double WindowHeight;
//     float SpeedFactor; void Load(); void Save() }
// Depends on: none
// Must not depend on: Avalonia and any UI type; Miller.App
