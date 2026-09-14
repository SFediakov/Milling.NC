// PLACEHOLDER - implemented by T-021 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Validation
// Purpose: Errors and warnings produced by ProjectValidator, each tied to a field name such as
//     Tool.CutterDiameter.
// Public interface (names only): sealed record ValidationMessage(string Field, string Message);
//     sealed class ValidationResult { IReadOnlyList<ValidationMessage> Errors;
//     IReadOnlyList<ValidationMessage> Warnings; bool IsValid }; sealed class ValidationException :
//     Exception { ValidationResult Result }
// Depends on: none
// Must not depend on: Avalonia and any UI type; Miller.App
