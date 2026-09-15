using System.Text;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;

namespace Miller.Application.Services;

// Writes the toolpath with the post-processor the project names. The post-processor is resolved
// before anything touches the disk, the extension is enforced, the directory is created, and the
// file is UTF-8 without a byte order mark. Returns the path actually written.
public sealed class ExportService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Export(Toolpath toolpath, MillingProject project, string appVersion, string path)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var post = PostProcessorRegistry.GetById(project.PostProcessorId);
        var target = EnforceExtension(path, post.FileExtension);
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(target, false, Utf8NoBom);
        post.Write(toolpath, project, appVersion, writer);
        return target;
    }

    public static string EnforceExtension(string path, string extension)
        => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, extension);
}
