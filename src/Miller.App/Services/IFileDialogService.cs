namespace Miller.App.Services;

// Abstraction over the storage provider so view models can be tested with a fake. Extensions are
// given without the dot ("stl", "json", "nc"); results are local paths or null when cancelled.
public interface IFileDialogService
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions, string? startDirectory);

    Task<string?> SaveFileAsync(string title, string defaultName, IReadOnlyList<string> extensions, string? startDirectory);
}
