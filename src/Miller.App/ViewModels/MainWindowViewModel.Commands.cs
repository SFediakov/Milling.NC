using CommunityToolkit.Mvvm.Input;
using Miller.App.Services;

namespace Miller.App.ViewModels;

// Project file, export and exit commands.
public sealed partial class MainWindowViewModel
{
    public const string NewProjectStatus = "New project";
    public const string DefaultExportName = "toolpath";
    public static readonly IReadOnlyList<string> ProjectExtensions = new[] { "json" };
    public static readonly IReadOnlyList<string> NcExtensions = new[] { "nc" };

    private bool CanExport => LastResult is not null && !IsBusy;

    [RelayCommand]
    private void NewProject()
    {
        Project.New();
        MeshImport.Clear();
        ClearResult();
        UpdateViewportScene();
        StatusText = NewProjectStatus;
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = await Dialogs.OpenFileAsync("Open project", ProjectExtensions, Settings.LastProjectDirectory);
        if (path is null)
        {
            return;
        }

        try
        {
            Project.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await ErrorDialog.ShowAsync(ex);
            return;
        }

        Settings.LastProjectDirectory = Path.GetDirectoryName(path);
        Settings.Save();
        ClearResult();
        MeshImport.Clear();
        UpdateViewportScene();
        StatusText = $"Opened {Path.GetFileName(path)}";
        await LoadProjectMeshesAsync(path);
    }

    [RelayCommand]
    private async Task SaveProjectAsync() => await SaveProjectCoreAsync();

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var path = await Dialogs.SaveFileAsync("Save project as", Path.GetFileName(Project.Path) ?? "project.miller.json", ProjectExtensions, Settings.LastProjectDirectory);
        if (path is null)
        {
            return;
        }

        await SaveToAsync(path);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportNcAsync()
    {
        var stlName = Project.Current.Models.Count > 0 ? Path.GetFileNameWithoutExtension(Project.Current.Models[0].StlPath) : string.Empty;
        var defaultName = (string.IsNullOrEmpty(stlName) ? DefaultExportName : stlName) + NcExtensions[0].Insert(0, ".");
        var path = await Dialogs.SaveFileAsync("Export NC", defaultName, NcExtensions, Settings.LastExportDirectory);
        if (path is null)
        {
            return;
        }

        try
        {
            var written = Export.Export(LastResult!.Toolpath, Project.Current, AppVersion, path);
            Settings.LastExportDirectory = Path.GetDirectoryName(written);
            Settings.Save();
            StatusText = $"Exported {written}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
        {
            await ErrorDialog.ShowAsync(ex);
        }
    }

    [RelayCommand]
    private async Task ExitAsync()
    {
        if (Machine.IsJobActive && !await Confirm.ConfirmAsync("A job is running on the machine. Stop it and exit?"))
        {
            return;
        }

        if (Project.IsDirty)
        {
            var decision = await Confirm.AskSaveChangesAsync();
            if (decision == SaveDecision.Cancel)
            {
                return;
            }

            if (decision == SaveDecision.Save && !await SaveProjectCoreAsync())
            {
                return;
            }
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    // Saves to the current file, or asks for one; false when the user cancelled or saving failed.
    private async Task<bool> SaveProjectCoreAsync()
    {
        if (Project.Path is not null)
        {
            return await SaveToAsync(Project.Path);
        }

        var path = await Dialogs.SaveFileAsync("Save project", "project.miller.json", ProjectExtensions, Settings.LastProjectDirectory);
        return path is not null && await SaveToAsync(path);
    }

    private async Task<bool> SaveToAsync(string path)
    {
        try
        {
            Project.SaveAs(path);
            Settings.LastProjectDirectory = Path.GetDirectoryName(path);
            Settings.Save();
            StatusText = $"Saved {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ErrorDialog.ShowAsync(ex);
            return false;
        }
    }
}
