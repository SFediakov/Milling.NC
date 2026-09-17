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
        if (project.SchemaVersion < MillingProject.OldestSchemaVersion || project.SchemaVersion > MillingProject.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project schema version {project.SchemaVersion}; this build reads versions {MillingProject.OldestSchemaVersion} to {MillingProject.CurrentSchemaVersion}.");
        }

        // Schema 1: the single StlPath becomes the first model placement.
        if (!string.IsNullOrEmpty(project.StlPath))
        {
            project.Models.Insert(0, new ModelPlacement { StlPath = project.StlPath });
        }

        // Schema 2: the roughing and finishing strategies and the milling direction are dropped; the
        // routing strategy keeps its default, "Z layer by layer", the level-based one of the two.
        project.StlPath = null;
        project.RoughingStrategyId = null;
        project.FinishingStrategyId = null;
        project.Parameters.Direction = null;

        // "3 axis precise" became "3 axis freedom" (one stepdown per pass) under a new id.
        if (project.RoutingStrategyId == Toolpaths.Strategies.ThreeAxisFreedomStrategy.LegacyStrategyId)
        {
            project.RoutingStrategyId = Toolpaths.Strategies.ThreeAxisFreedomStrategy.StrategyId;
        }

        project.SchemaVersion = MillingProject.CurrentSchemaVersion;
        return project;
    }
}
