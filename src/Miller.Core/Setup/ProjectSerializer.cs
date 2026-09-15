using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Core.Setup;

public static class ProjectSerializer
{
    // Vector3 exposes public fields, hence IncludeFields. Unknown members are an error so a typo in
    // a hand-edited file is reported instead of silently ignored. JSON numbers are culture-free.
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(MillingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static MillingProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var project = JsonSerializer.Deserialize<MillingProject>(json, Options)
            ?? throw new InvalidDataException("Project JSON is null.");
        if (project.SchemaVersion != MillingProject.CurrentSchemaVersion && project.SchemaVersion != MillingProject.LegacySchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project schema version {project.SchemaVersion}; this build reads versions {MillingProject.LegacySchemaVersion} and {MillingProject.CurrentSchemaVersion}.");
        }

        // Schema 1: the single StlPath becomes the first model placement.
        if (!string.IsNullOrEmpty(project.StlPath))
        {
            project.Models.Insert(0, new ModelPlacement { StlPath = project.StlPath });
        }

        project.StlPath = null;
        project.SchemaVersion = MillingProject.CurrentSchemaVersion;
        return project;
    }
}
