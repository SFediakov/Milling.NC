using System.Text.Json;

namespace Miller.Core.Setup;

// A named copy of the settings of the Tool, Axes, Cutting and Strategy tabs. Stock and models are
// not part of it: they belong to the job, the preset to the machine and the cutter.
public sealed class MillingPreset
{
    public string Name { get; set; } = string.Empty;

    public ToolDefinition Tool { get; set; } = ToolDefinition.Default();

    public AxisSetup Axes { get; set; } = AxisSetup.Default();

    public CuttingParameters Parameters { get; set; } = CuttingParameters.Default();

    public string RoutingStrategyId { get; set; } = MillingProject.DefaultRoutingStrategyId;

    public string PostProcessorId { get; set; } = MillingProject.DefaultPostProcessorId;

    public CutScope CutScope { get; set; } = CutScope.Everything;

    public float MinIslandVolume { get; set; }

    public float ReachPercent { get; set; } = HeightMaps.ReachMap.DefaultPercent;

    // Snapshot of the project's settings; the preset owns copies, so later edits of the project do
    // not leak into it.
    public static MillingPreset FromProject(MillingProject project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Clone(new MillingPreset
        {
            Name = name.Trim(),
            Tool = project.Tool,
            Axes = project.Axes,
            Parameters = project.Parameters,
            RoutingStrategyId = project.RoutingStrategyId,
            PostProcessorId = project.PostProcessorId,
            CutScope = project.CutScope,
            MinIslandVolume = project.MinIslandVolume,
            ReachPercent = project.ReachPercent,
        });
    }

    // Writes copies of the preset's settings into the project; the preset stays untouched.
    public void ApplyTo(MillingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var copy = Clone(this);
        project.Tool = copy.Tool;
        project.Axes = copy.Axes;
        project.Parameters = copy.Parameters;
        project.RoutingStrategyId = copy.RoutingStrategyId;
        project.PostProcessorId = copy.PostProcessorId;
        project.CutScope = copy.CutScope;
        project.MinIslandVolume = copy.MinIslandVolume;
        project.ReachPercent = copy.ReachPercent;
    }

    // Deep copy through the project JSON options: the setup classes are plain mutable data.
    public static MillingPreset Clone(MillingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Deserialize<MillingPreset>(JsonSerializer.Serialize(preset, ProjectSerializer.Options), ProjectSerializer.Options)
            ?? throw new InvalidDataException("Preset JSON is null.");
    }
}
