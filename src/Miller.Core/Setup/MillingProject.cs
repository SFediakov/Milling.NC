namespace Miller.Core.Setup;

// Everything a .miller.json file contains.
public sealed class MillingProject
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultRoughingStrategyId = "raster-roughing";
    public const string DefaultFinishingStrategyId = "raster-finishing";
    public const string DefaultPostProcessorId = "grbl";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string StlPath { get; set; } = string.Empty;

    public ToolDefinition Tool { get; set; } = ToolDefinition.Default();

    public StockDefinition Stock { get; set; } = StockDefinition.Default();

    public AxisSetup Axes { get; set; } = AxisSetup.Default();

    public CuttingParameters Parameters { get; set; } = CuttingParameters.Default();

    public string RoughingStrategyId { get; set; } = DefaultRoughingStrategyId;

    public string FinishingStrategyId { get; set; } = DefaultFinishingStrategyId;

    public string PostProcessorId { get; set; } = DefaultPostProcessorId;

    public static MillingProject Default() => new();
}
