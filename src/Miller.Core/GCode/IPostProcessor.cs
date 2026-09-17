using Miller.Core.Setup;
using Miller.Core.Toolpaths;

namespace Miller.Core.GCode;

// The exchangeable G-code dialect writer. Implementations are listed in PostProcessorRegistry.
// The application version is passed in because the version constant lives in the App project.
public interface IPostProcessor
{
    // Lowercase letters, digits and hyphens; unique across the registry.
    string Id { get; }

    string DisplayName { get; }

    // With the leading dot, for example ".nc".
    string FileExtension { get; }

    void Write(Toolpath toolpath, MillingProject project, string appVersion, TextWriter writer);
}
