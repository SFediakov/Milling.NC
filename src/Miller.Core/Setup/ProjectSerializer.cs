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
        Converters = { new CollisionModeConverter(), new JsonStringEnumConverter() },
    };

    // Strategies removed in T-214 ("three-axis-precise" is the id "3 axis freedom" had before): files naming
    // them load with "Z layer by layer", the one strategy left.
    public static readonly IReadOnlyList<string> RemovedStrategyIds = new[] { "three-axis-freedom", "three-axis-precise" };

    public static string CurrentStrategyId(string id) => RemovedStrategyIds.Contains(id, StringComparer.Ordinal) ? MillingProject.DefaultRoutingStrategyId : id;

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

        // T-214: the removed strategy and collision modes and their settings are dropped (the collision
        // mode by CollisionModeConverter); T-215: the plunge rate is dropped.
        project.RoutingStrategyId = CurrentStrategyId(project.RoutingStrategyId);
        project.RecursionRatio = null;
        project.OneRunRatio = null;
        project.Parameters.FinishingStepover = null;
        project.Parameters.PlungeRate = null;

        project.SchemaVersion = MillingProject.CurrentSchemaVersion;
        return project;
    }
}
