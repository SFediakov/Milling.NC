using Miller.App.Services;
using Miller.App.ViewModels;
using Miller.Application.Services;

namespace Miller.Tests.Fixtures;

public sealed class FakeFileDialogService : IFileDialogService
{
    public Queue<string?> OpenResults { get; } = new();

    public Queue<string?> SaveResults { get; } = new();

    public List<string> Titles { get; } = new();

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions, string? startDirectory)
    {
        Titles.Add(title);
        return Task.FromResult(OpenResults.Count > 0 ? OpenResults.Dequeue() : null);
    }

    public Task<string?> SaveFileAsync(string title, string defaultName, IReadOnlyList<string> extensions, string? startDirectory)
    {
        Titles.Add(title);
        return Task.FromResult(SaveResults.Count > 0 ? SaveResults.Dequeue() : null);
    }
}

public sealed class FakeErrorDialogService : IErrorDialogService
{
    public List<Exception> Shown { get; } = new();

    public Task ShowAsync(Exception exception)
    {
        Shown.Add(exception);
        return Task.CompletedTask;
    }
}

public sealed class FakeConfirmDialogService : IConfirmDialogService
{
    public Queue<SaveDecision> Answers { get; } = new();

    public int Asked { get; private set; }

    public Task<SaveDecision> AskSaveChangesAsync()
    {
        Asked++;
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : SaveDecision.Cancel);
    }
}

// Real services rooted in a temporary directory, with fake dialogs.
public static class TestServices
{
    public const string Version = "Build_0.0.0";

    public static MainWindowViewModel MainWindowViewModel(string tempRoot, FakeFileDialogService? dialogs = null, FakeErrorDialogService? errors = null, FakeConfirmDialogService? confirm = null)
        => new(
            new ProjectService(),
            new MeshImportService(),
            new PipelineService(),
            new ExportService(),
            new SettingsService(Path.Combine(tempRoot, "settings")),
            dialogs ?? new FakeFileDialogService(),
            errors ?? new FakeErrorDialogService(),
            confirm ?? new FakeConfirmDialogService(),
            Version);
}
