namespace Miller.Application.Progress;

// Passed through IProgress<ProgressReport> from services to the UI. Fraction runs 0..1 over the whole
// operation, not per stage.
public readonly record struct ProgressReport(string Stage, float Fraction, string Message);
