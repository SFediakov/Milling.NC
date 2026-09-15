using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Application.Validation;

namespace Miller.App.ViewModels;

// Per-field validation messages shared by every settings view model.
public abstract class ViewModelBase : ObservableObject
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _warnings = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    public string? ErrorFor(string field) => _errors.GetValueOrDefault(field);

    public string? WarningFor(string field) => _warnings.GetValueOrDefault(field);

    public void SetError(string field, string message)
    {
        _errors[field] = message;
        OnErrorsChanged();
    }

    public void ClearErrors()
    {
        if (_errors.Count == 0)
        {
            return;
        }

        _errors.Clear();
        OnErrorsChanged();
    }

    // Keeps the messages whose field starts with the prefix, for example "Tool." for the tool panel.
    public void ApplyValidation(ValidationResult result, string prefix)
    {
        ArgumentNullException.ThrowIfNull(result);
        _errors.Clear();
        _warnings.Clear();
        foreach (var error in result.Errors)
        {
            if (error.Field.StartsWith(prefix, StringComparison.Ordinal))
            {
                _errors[error.Field] = error.Message;
            }
        }

        foreach (var warning in result.Warnings)
        {
            if (warning.Field.StartsWith(prefix, StringComparison.Ordinal))
            {
                _warnings[warning.Field] = warning.Message;
            }
        }

        OnErrorsChanged();
    }

    protected virtual void OnErrorsChanged()
    {
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(HasErrors));
    }
}
