namespace Miller.Application.Validation;

public sealed record ValidationMessage(string Field, string Message);

public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationMessage> errors, IReadOnlyList<ValidationMessage> warnings)
    {
        Errors = errors;
        Warnings = warnings;
    }

    public IReadOnlyList<ValidationMessage> Errors { get; }

    public IReadOnlyList<ValidationMessage> Warnings { get; }

    public bool IsValid => Errors.Count == 0;
}

public sealed class ValidationException : Exception
{
    public ValidationException(ValidationResult result)
        : base(Describe(result))
    {
        Result = result;
    }

    public ValidationResult Result { get; }

    private static string Describe(ValidationResult result)
        => "Project is invalid: " + string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}"));
}
