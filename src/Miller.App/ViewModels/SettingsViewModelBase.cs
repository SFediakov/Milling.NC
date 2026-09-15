using System.Runtime.CompilerServices;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Setup;

namespace Miller.App.ViewModels;

// Shared plumbing of the settings panels: an edit goes to the project, marks it dirty and runs the
// validator; a project change (new, load, an edit in another panel) reloads the panel without
// marking it dirty.
public abstract class SettingsViewModelBase : ViewModelBase
{
    private bool _reloading;

    protected SettingsViewModelBase(ProjectService project, string fieldPrefix)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        FieldPrefix = fieldPrefix;
        Project.ProjectChanged += (_, _) => Reload();
    }

    public ProjectService Project { get; }

    public string FieldPrefix { get; }

    protected MillingProject Current => Project.Current;

    public void Revalidate() => ApplyValidation(ProjectValidator.Validate(Current, ModelBoundsForValidation()), FieldPrefix);

    protected void Edit(Action<MillingProject> change, [CallerMemberName] string? property = null)
    {
        if (_reloading)
        {
            return;
        }

        change(Current);
        if (property is not null)
        {
            OnPropertyChanged(property);
        }

        Project.MarkDirty();
        Revalidate();
    }

    protected virtual BoundingBox? ModelBoundsForValidation() => null;

    // Derived panels raise a change for every bound property here.
    protected abstract void OnReload();

    private void Reload()
    {
        _reloading = true;
        try
        {
            OnReload();
        }
        finally
        {
            _reloading = false;
        }

        Revalidate();
    }
}
