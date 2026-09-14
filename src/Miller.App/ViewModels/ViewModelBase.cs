// PLACEHOLDER - implemented by T-059 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.ViewModels
// Purpose: ObservableObject base with per-field error dictionary used by every settings view model.
// Public interface (names only): abstract class ViewModelBase : ObservableObject {
//     IReadOnlyDictionary<string, string> Errors; void SetError(string field, string message); void
//     ClearErrors(); void ApplyValidation(ValidationResult result, string prefix) }
// Depends on: Miller.Application services, CommunityToolkit.Mvvm
// Must not depend on: Miller.Core algorithms called directly from views or view models (go through
//     Miller.Application services)
