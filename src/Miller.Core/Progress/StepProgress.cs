namespace Miller.Core.Progress;

// Progress inside one pipeline stage, reported by Core producers that know nothing about the stage
// table: Step is the 1-based inner iteration (reach map round, routing pass) of Steps, Fraction the
// share 0..1 of the whole stage done so far. Steps 0 means the stage has no inner loop.
public readonly record struct StepProgress(int Step, int Steps, float Fraction);
