using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Miller.App.Services;

// Storage-provider implementation bound to the main window, resolved lazily because the window is
// created after the view model that needs the dialogs.
public sealed class FileDialogService : IFileDialogService
{
    private readonly Func<TopLevel?> _owner;

    public FileDialogService(Func<TopLevel?> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions, string? startDirectory)
    {
        var provider = Provider();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(provider, startDirectory),
            FileTypeFilter = new[] { Filter(extensions) },
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string defaultName, IReadOnlyList<string> extensions, string? startDirectory)
    {
        var provider = Provider();
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            DefaultExtension = extensions[0],
            SuggestedStartLocation = await StartFolderAsync(provider, startDirectory),
            FileTypeChoices = new[] { Filter(extensions) },
        });
        return file?.TryGetLocalPath();
    }

    private IStorageProvider Provider()
        => (_owner() ?? throw new InvalidOperationException("The main window is not available for file dialogs.")).StorageProvider;

    private static FilePickerFileType Filter(IReadOnlyList<string> extensions)
        => new(string.Join(", ", extensions.Select(e => e.ToUpperInvariant())))
        {
            Patterns = extensions.Select(e => "*." + e).ToArray(),
        };

    private static async Task<IStorageFolder?> StartFolderAsync(IStorageProvider provider, string? directory)
        => string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : await provider.TryGetFolderFromPathAsync(directory);
}
