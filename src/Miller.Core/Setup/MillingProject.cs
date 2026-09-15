namespace Miller.Core.Setup;

// Everything a .miller.json file contains.
public sealed class MillingProject
{
    // Schema 2 holds a list of models; schema 1 files with one StlPath are migrated on load.
    public const int CurrentSchemaVersion = 2;
    public const int LegacySchemaVersion = 1;
    // Layer complete clears the strip beside walls at every level, which the head limit relies on.
    public const string DefaultRoughingStrategyId = "layer-complete";
    public const string DefaultFinishingStrategyId = "raster-finishing";
    public const string DefaultPostProcessorId = "grbl";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<ModelPlacement> Models { get; set; } = new();

    // Schema 1 field: read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StlPath { get; set; }

    public ToolDefinition Tool { get; set; } = ToolDefinition.Default();

    public StockDefinition Stock { get; set; } = StockDefinition.Default();

    public AxisSetup Axes { get; set; } = AxisSetup.Default();

    public CuttingParameters Parameters { get; set; } = CuttingParameters.Default();

    public string RoughingStrategyId { get; set; } = DefaultRoughingStrategyId;

    public string FinishingStrategyId { get; set; } = DefaultFinishingStrategyId;

    public string PostProcessorId { get; set; } = DefaultPostProcessorId;

    public static MillingProject Default() => new();
}
